using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace VoiceBridge.Audio;

/// <summary>
/// Захват звука одного процесса вместе с дочерними окнами
/// (ActivateAudioInterfaceAsync + AUDIOCLIENT_ACTIVATION_PARAMS, Windows 10 2004+).
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn, IDisposable
{
    private const string ProcessLoopbackDevice = @"VAD\Process_Loopback";

    // Активированный клиент кэшируется на процесс: Windows отклоняет ПОВТОРНУЮ
    // активацию того же target-PID (E_UNEXPECTED), пока живёт предыдущая, — поэтому
    // один клиент переиспользуется через Stop/Start и не освобождается до выхода.
    private sealed class SharedClient
    {
        public AudioClient Client = null!;
        public AudioCaptureClient Capture = null!;
        public int Users;
        public bool Started;
        public DateTime TargetStart;
    }

    private static readonly Dictionary<int, SharedClient> Shared = new();
    private static readonly object SharedGate = new();

    private readonly int _pid;
    private SharedClient? _sc;
    private AudioClient? _client;
    private AudioCaptureClient? _capture;
    private Thread? _thread;
    private volatile bool _run;

    public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public CaptureState CaptureState { get; private set; } = CaptureState.Stopped;
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public int ProcessId => _pid;

    public ProcessLoopbackCapture(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "PID процесса должен быть больше нуля.");

        _pid = processId;
        DateTime start = GetStartTimeSafe(processId);

        lock (SharedGate)
        {
            if (Shared.TryGetValue(processId, out var existing))
            {
                if (existing.TargetStart == start)
                {
                    existing.Users++;
                    _sc = existing;
                    _client = existing.Client;
                    _capture = existing.Capture;
                    return;
                }
                // PID переиспользован другой программой — старый клиент выбрасываем.
                Shared.Remove(processId);
                try { existing.Client.Dispose(); } catch { /* ignore */ }
            }
        }

        // Важно: активированный IAudioClient диспатчится только с MTA-потоков,
        // со STA (UI) он отдаёт E_NOINTERFACE — поэтому Initialize и GetService
        // выполняем здесь же, внутри пула, сразу после активации.
        // Повторная активация при сбое: сразу после остановки другого роута Windows
        // может вернуть E_UNEXPECTED на Initialize — тогда пробуем заново.
        var (client, capture) = Task.Run(async () =>
        {
            for (int attempt = 1; ; attempt++)
            {
                var c = await ActivateAsync().ConfigureAwait(false);
                try
                {
                    c.Initialize(AudioClientShareMode.Shared,
                        AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm,
                        0, 0, WaveFormat, Guid.Empty);
                    return (c, c.AudioCaptureClient);
                }
                catch (Exception) when (attempt < 4)
                {
                    try { c.Dispose(); } catch { /* ignore */ }
                    await Task.Delay(300).ConfigureAwait(false);
                }
            }
        }).GetAwaiter().GetResult();

        lock (SharedGate)
        {
            // Проигравший гонку клиент отдаём владельцу, а себе берём уже вставленный.
            if (Shared.TryGetValue(processId, out var raced) && raced.TargetStart == start)
            {
                raced.Users++;
                try { client.Dispose(); } catch { /* ignore */ }
                _sc = raced;
                _client = raced.Client;
                _capture = raced.Capture;
                return;
            }

            var sc = new SharedClient
            {
                Client = client,
                Capture = capture,
                Users = 1,
                TargetStart = start
            };
            Shared[processId] = sc;
            _sc = sc;
            _client = client;
            _capture = capture;
        }
    }

    private static DateTime GetStartTimeSafe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static void CallOnMta(Action action) =>
        Task.Run(action).GetAwaiter().GetResult();

    private async Task<AudioClient> ActivateAsync()
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = (uint)_pid,
                ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree
            }
        };

        int activationSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr activationPtr = Marshal.AllocHGlobal(activationSize);
        IntPtr variantPtr = Marshal.AllocHGlobal(24); // PROPVARIANT (x64)
        try
        {
            Marshal.StructureToPtr(activation, activationPtr, false);
            Marshal.WriteInt16(variantPtr, 0, (short)VarEnum.VT_BLOB); // vt
            Marshal.WriteInt32(variantPtr, 8, activationSize);         // blob.cbSize
            Marshal.WriteIntPtr(variantPtr, 16, activationPtr);        // blob.pBlobData

            var handler = new ProcessLoopbackActivationHandler();
            Guid iid = typeof(IAudioClient).GUID;
            int hrStart = ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref iid, variantPtr, handler, out var asyncOp);
            if (hrStart < 0)
                Marshal.ThrowExceptionForHR(hrStart);

            object activated;
            try
            {
                try
                {
                    activated = await handler.Task.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"шаг GetActivateResult: {ex.Message}", ex);
                }
            }
            finally
            {
                // IActivateAudioInterfaceAsyncOperation обязательно отпускаем:
                // иначе RCW держит нативную ссылку на активацию этого PID,
                // и повторный захват того же процесса падает с E_UNEXPECTED.
                if (asyncOp is not null)
                {
                    try { Marshal.ReleaseComObject(asyncOp); } catch { /* ignore */ }
                }
            }

            if (activated is not IAudioClient audioClient)
                throw new InvalidOperationException("Активация вернула не IAudioClient.");
            return new AudioClient(audioClient);
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(activationPtr);
        }
    }

    [DllImport("mmdevapi.dll", CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation asyncOperation);

    public void StartRecording()
    {
        if (CaptureState == CaptureState.Capturing) return;
        if (_client is null || _capture is null)
            throw new InvalidOperationException("Захват процесса не инициализирован.");

        lock (SharedGate)
        {
            if (_sc is null || !_sc.Started)
            {
                CallOnMta(() => _client!.Start());
                if (_sc is not null) _sc.Started = true;
            }
        }
        CaptureState = CaptureState.Capturing;
        _run = true;
        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "VoiceBridge.ProcessLoopback"
        };
        _thread.Start();
    }

    public void StopRecording()
    {
        if (CaptureState != CaptureState.Capturing) return;
        _run = false;
        var thread = _thread;
        if (thread is not null && thread.IsAlive)
            thread.Join(1500);
        _thread = null;
        lock (SharedGate)
        {
            if (_sc is null || _sc.Started)
            {
                try { CallOnMta(() => _client?.Stop()); }
                catch { /* устройство могло уже отвалиться */ }
                if (_sc is not null) _sc.Started = false;
            }
        }
        CaptureState = CaptureState.Stopped;
    }

    private void ReadLoop()
    {
        Exception? error = null;
        try
        {
            var capture = _capture!;
            int blockAlign = WaveFormat.BlockAlign;

            while (_run)
            {
                int packet = capture.GetNextPacketSize();
                while (packet > 0 && _run)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags, out _, out _);
                    if (frames > 0)
                    {
                        int bytes = frames * blockAlign;
                        var buffer = new byte[bytes];
                        if (data != IntPtr.Zero && !flags.HasFlag(AudioClientBufferFlags.Silent))
                            Marshal.Copy(data, buffer, 0, bytes);
                        DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                    }
                    capture.ReleaseBuffer(frames);
                    packet = capture.GetNextPacketSize();
                }
                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        CaptureState = CaptureState.Stopped;
        RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    public void Dispose()
    {
        StopRecording();
        _run = false;
        lock (SharedGate)
        {
            if (_sc is not null)
            {
                if (_sc.Users > 0) _sc.Users--;
                // Клиент намеренно НЕ освобождаем: повторная активация того же PID
                // падает с E_UNEXPECTED; следующий захват переиспользует его через Stop/Start.
                _sc = null;
            }
        }
        _client = null;
        _capture = null;
        GC.SuppressFinalize(this);
    }
}

// ===== Маршалинговые конструкции Win32 (аналоги audioclientactivationparams.h) =====

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1
}

internal enum ProcessLoopbackMode
{
    IncludeTargetProcessTree = 0,
    ExcludeTargetProcessTree = 1
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

/// <summary>Завершение ActivateAudioInterfaceAsync: собирает активированный IAudioClient.</summary>
[ComVisible(true)]
internal sealed class ProcessLoopbackActivationHandler : IActivateAudioInterfaceCompletionHandler
{
    private readonly TaskCompletionSource<object> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<object> Task => _tcs.Task;

    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        try
        {
            operation.GetActivateResult(out int hr, out object? activated);
            if (hr < 0)
                _tcs.TrySetException(new COMException("Активация аудиоинтерфейса не удалась.", hr));
            else if (activated is null)
                _tcs.TrySetException(new InvalidOperationException("Активация вернула пустой интерфейс."));
            else
                _tcs.TrySetResult(activated);
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }
}
