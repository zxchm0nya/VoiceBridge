using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceBridge.Audio;

public sealed class AudioEngine : IDisposable
{
    public sealed class StartOptions
    {
        public string? SourceId { get; init; }
        public string? TargetId { get; init; }
        public bool MicMix { get; init; }
        public string? MicId { get; init; }
        public float SystemGain { get; init; } = 1f;
        public float MicGain { get; init; } = 1f;
        public int LatencyMs { get; init; } = 50;

        /// <summary>PID процесса для захвата только его звука; null/0 — вся система.</summary>
        public int? CapturePid { get; init; }

        /// <summary>PID'ы для режима «по приложениям» (захват каждого отдельно + мут). null — обычный режим.</summary>
        public IReadOnlyList<int>? CapturePids { get; init; }
    }

    private readonly object _gate = new();

    private IWaveIn? _sysCap;
    private WasapiCapture? _micCap;
    private WasapiOut? _out;
    private BufferedWaveProvider? _sysBuf;
    private BufferedWaveProvider? _micBuf;
    private readonly Dictionary<int, AppCapture> _apps = new();
    private MixingSampleProvider? _mixer;
    private bool _multiMode;

    private volatile float _sysGain = 1f;
    private volatile float _micGain = 1f;
    private volatile bool _sysMuted;
    private volatile bool _micMuted;
    private volatile float _sysPeak;
    private volatile float _micPeak;
    private volatile float _outPeak;
    private volatile bool _running;
    private volatile string? _lastError;

    public bool IsRunning => _running;
    public string? LastError => _lastError;
    public float SystemPeak => _sysPeak;
    public float MicPeak => _micPeak;
    public float OutputPeak => _outPeak;
    public WaveFormat? OutputFormat { get; private set; }
    public string? SourceName { get; private set; }
    public string? TargetName { get; private set; }

    public void SetGains(float system, float mic)
    {
        _sysGain = Math.Clamp(system, 0f, 4f);
        _micGain = Math.Clamp(mic, 0f, 4f);
    }

    public void SetMuted(bool system, bool mic)
    {
        _sysMuted = system;
        _micMuted = mic;
    }

    public void Start(StartOptions o)
    {
        lock (_gate)
        {
            StopInternal();
            _lastError = null;
            try
            {
                var src = ResolveDevice(o.SourceId, DataFlow.Render);
                var dst = ResolveDevice(o.TargetId, DataFlow.Render);
                if (src is null) throw new InvalidOperationException("Не найдено устройство-источник (наушники).");
                if (dst is null) throw new InvalidOperationException("Не найдено целевое устройство (виртуальный кабель).");
                if (src.ID == dst.ID)
                    throw new InvalidOperationException("Источник и цель совпадают — выберите разные устройства, иначе получится петля.");

                SourceName = src.FriendlyName;
                TargetName = dst.FriendlyName;

                _sysGain = Math.Clamp(o.SystemGain, 0f, 4f);
                _micGain = Math.Clamp(o.MicGain, 0f, 4f);
                _sysMuted = false;
                _micMuted = false;

                var mix = dst.AudioClient.MixFormat;
                OutputFormat = mix;
                _multiMode = o.CapturePids is not null;

                ISampleProvider? sysInput = null;
                if (!_multiMode)
                {
                    _sysCap = o.CapturePid is int pid && pid > 0
                        ? new ProcessLoopbackCapture(pid)
                        : new WasapiLoopbackCapture(src);
                    _sysCap.DataAvailable += OnSysData;
                    _sysCap.RecordingStopped += OnSysStopped;
                    _sysBuf = new BufferedWaveProvider(_sysCap.WaveFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(600),
                        DiscardOnBufferOverflow = true
                    };
                    sysInput = BuildChain(_sysBuf, mix);
                }
                else
                {
                    var pids = o.CapturePids!;
                    if (pids.Count == 0)
                        throw new InvalidOperationException("Список приложений для захвата пуст.");
                    foreach (int appPid in pids.Distinct())
                    {
                        ProcessLoopbackCapture cap;
                        try
                        {
                            cap = new ProcessLoopbackCapture(appPid);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                $"Не удалось захватить приложение (pid {appPid}): {ex.Message}", ex);
                        }
                        _apps[appPid] = CreateAppCapture(appPid, cap, mix);
                    }
                }

                ISampleProvider? micInput = null;
                if (o.MicMix)
                {
                    var micDev = ResolveDevice(o.MicId, DataFlow.Capture)
                                 ?? ResolveDevice(null, DataFlow.Capture);
                    if (micDev is not null)
                    {
                        _micCap = new WasapiCapture(micDev);
                        _micCap.DataAvailable += OnMicData;
                        _micCap.RecordingStopped += OnMicStopped;
                        _micBuf = new BufferedWaveProvider(_micCap.WaveFormat)
                        {
                            BufferDuration = TimeSpan.FromMilliseconds(600),
                            DiscardOnBufferOverflow = true
                        };
                        micInput = BuildChain(_micBuf, mix);
                    }
                }

                ISampleProvider final;
                if (_multiMode || micInput is not null)
                {
                    var inputs = new List<ISampleProvider>();
                    if (_multiMode)
                    {
                        foreach (var ac in _apps.Values)
                            inputs.Add(ac.Pad);
                    }
                    else
                    {
                        inputs.Add(new PadSampleProvider(sysInput!));
                    }
                    if (micInput is not null)
                        inputs.Add(new PadSampleProvider(micInput));
                    _mixer = new MixingSampleProvider(inputs) { ReadFully = true };
                    final = _mixer;
                }
                else
                {
                    final = sysInput!;
                }

                final = new ClampSampleProvider(final);
                var tap = new PeakTapSampleProvider(final, p => _outPeak = p);

                IWaveProvider wvp;
                if (IsFloatFormat(mix))
                    wvp = new SampleToWaveProvider(tap);
                else if (IsPcm16Format(mix))
                    wvp = new SampleToWaveProvider16(tap);
                else
                    throw new InvalidOperationException($"Неподдерживаемый формат вывода: {mix.Encoding}, {mix.BitsPerSample} бит");

                _out = new WasapiOut(dst, AudioClientShareMode.Shared, true,
                    Math.Clamp(o.LatencyMs, 10, 400));
                _out.Init(wvp);

                _sysCap?.StartRecording();
                if (_multiMode)
                {
                    foreach (var ac in _apps.Values)
                        ac.Cap.StartRecording();
                }
                _micCap?.StartRecording();
                _out.Play();

                _sysPeak = 0f;
                _micPeak = 0f;
                _outPeak = 0f;
                _running = true;
            }
            catch (Exception ex)
            {
                StopInternal();
                _lastError = ex.Message;
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        _running = false;

        if (_sysCap is not null)
        {
            try { _sysCap.StopRecording(); } catch { /* ignore */ }
        }
        if (_micCap is not null)
        {
            try { _micCap.StopRecording(); } catch { /* ignore */ }
        }
        if (_out is not null)
        {
            try { _out.Stop(); } catch { /* ignore */ }
        }

        if (_sysCap is not null)
        {
            _sysCap.DataAvailable -= OnSysData;
            _sysCap.RecordingStopped -= OnSysStopped;
            try { _sysCap.Dispose(); } catch { /* ignore */ }
        }
        if (_micCap is not null)
        {
            _micCap.DataAvailable -= OnMicData;
            _micCap.RecordingStopped -= OnMicStopped;
            try { _micCap.Dispose(); } catch { /* ignore */ }
        }
        if (_out is not null)
        {
            try { _out.Dispose(); } catch { /* ignore */ }
        }

        foreach (var ac in _apps.Values)
        {
            try { ac.Cap.StopRecording(); } catch { /* ignore */ }
        }
        foreach (var ac in _apps.Values)
        {
            if (ac.Handler is not null)
            {
                try { ac.Cap.DataAvailable -= ac.Handler; } catch { /* ignore */ }
            }
            try { ac.Cap.Dispose(); } catch { /* ignore */ }
        }
        _apps.Clear();
        _mixer = null;
        _multiMode = false;

        _sysCap = null;
        _micCap = null;
        _out = null;
        _sysBuf = null;
        _micBuf = null;
        _sysPeak = 0f;
        _micPeak = 0f;
        _outPeak = 0f;
    }

    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private static bool IsFloatFormat(WaveFormat f)
    {
        if (f.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        return f.Encoding == WaveFormatEncoding.Extensible
               && f is WaveFormatExtensible ext && ext.SubFormat == SubtypeIeeeFloat;
    }

    private static bool IsPcm16Format(WaveFormat f)
    {
        if (f.BitsPerSample != 16) return false;
        if (f.Encoding == WaveFormatEncoding.Pcm) return true;
        return f.Encoding == WaveFormatEncoding.Extensible
               && f is WaveFormatExtensible ext && ext.SubFormat == SubtypePcm;
    }

    private static ISampleProvider BuildChain(IWaveProvider buffer, WaveFormat targetMix)
    {
        ISampleProvider sp = buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels != targetMix.Channels)
            sp = new ChannelAdapterSampleProvider(sp, targetMix.Channels);
        if (sp.WaveFormat.SampleRate != targetMix.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, targetMix.SampleRate);
        return sp;
    }

    /// <summary>
    /// Гарантирует полный ответ count: MixingSampleProvider навсегда удаляет вход,
    /// вернувший меньше count, а BufferedWaveProvider часто возвращает частичные данные.
    /// </summary>
    private sealed class PadSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        public PadSampleProvider(ISampleProvider src) => _src = src;
        public WaveFormat WaveFormat => _src.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _src.Read(buffer, offset, count);
            if (read < count)
                Array.Clear(buffer, offset + read, count - read);
            return count;
        }
    }

    /// <summary>Захват одного приложения в режиме «по приложениям».</summary>
    private sealed class AppCapture
    {
        public int Pid { get; init; }
        public ProcessLoopbackCapture Cap { get; init; } = null!;
        public BufferedWaveProvider Buf { get; init; } = null!;
        public PadSampleProvider Pad { get; init; } = null!;
        public EventHandler<WaveInEventArgs>? Handler { get; set; }
        public volatile float Gain = 1f;
    }

    private AppCapture CreateAppCapture(int pid, ProcessLoopbackCapture cap, WaveFormat mix)
    {
        var buf = new BufferedWaveProvider(cap.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(600),
            DiscardOnBufferOverflow = true
        };
        var ac = new AppCapture
        {
            Pid = pid,
            Cap = cap,
            Buf = buf,
            Pad = new PadSampleProvider(BuildChain(buf, mix))
        };
        ac.Handler = (_, e) => OnAppData(ac, e);
        cap.DataAvailable += ac.Handler;
        return ac;
    }

    private void OnAppData(AppCapture ac, WaveInEventArgs e)
    {
        try
        {
            float gain = ac.Gain;
            float peak = ApplyGain(e, ac.Cap.WaveFormat, gain);
            if (gain > 0f)
                _sysPeak = peak;
            ac.Buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch
        {
            /* ignore transient race during stop */
        }
    }

    /// <summary>Мультирежим активен (захват идёт по списку приложений).</summary>
    public bool MultiAppActive
    {
        get { lock (_gate) return _running && _multiMode; }
    }

    /// <summary>PID'ы приложений, захватываемых прямо сейчас.</summary>
    public IReadOnlyList<int> ActiveAppPids
    {
        get { lock (_gate) return _apps.Keys.ToArray(); }
    }

    /// <summary>Подключить приложение к захвату на лету (режим «по приложениям»).</summary>
    public void AddApp(int pid)
    {
        lock (_gate)
        {
            if (!_running || !_multiMode || pid <= 0 || _apps.ContainsKey(pid))
                return;
            var cap = new ProcessLoopbackCapture(pid);
            var ac = CreateAppCapture(pid, cap, OutputFormat!);
            try
            {
                _mixer!.AddMixerInput(ac.Pad);
                cap.StartRecording();
                _apps[pid] = ac;
            }
            catch
            {
                try { _mixer!.RemoveMixerInput(ac.Pad); } catch { /* ignore */ }
                try { cap.Dispose(); } catch { /* ignore */ }
                throw;
            }
        }
    }

    /// <summary>Отключить и освободить захват приложения.</summary>
    public void RemoveApp(int pid)
    {
        lock (_gate)
        {
            if (!_apps.TryGetValue(pid, out var ac))
                return;
            _apps.Remove(pid);
            if (ac.Handler is not null)
            {
                try { ac.Cap.DataAvailable -= ac.Handler; } catch { /* ignore */ }
            }
            try { _mixer?.RemoveMixerInput(ac.Pad); } catch { /* ignore */ }
            try { ac.Cap.StopRecording(); } catch { /* ignore */ }
            try { ac.Cap.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>Заглушить/включить звук конкретного приложения (голос через микс не затрагивается).</summary>
    public void SetAppMuted(int pid, bool muted)
    {
        lock (_gate)
        {
            if (_apps.TryGetValue(pid, out var ac))
                ac.Gain = muted ? 0f : 1f;
        }
    }

    private static MMDevice? ResolveDevice(string? id, DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var d = en.GetDevice(id);
                if (d.DataFlow == flow) return d;
            }
            catch
            {
                /* fall through to default */
            }
        }
        try
        {
            return en.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }
        catch
        {
            return null;
        }
    }

    private void OnSysData(object? sender, WaveInEventArgs e)
    {
        var buf = _sysBuf;
        if (buf is null) return;
        try
        {
            float gain = _sysMuted ? 0f : _sysGain;
            _sysPeak = ApplyGain(e, _sysCap!.WaveFormat, gain);
            buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch
        {
            /* ignore transient race during stop */
        }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        var buf = _micBuf;
        if (buf is null) return;
        try
        {
            float gain = _micMuted ? 0f : _micGain;
            _micPeak = ApplyGain(e, _micCap!.WaveFormat, gain);
            buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch
        {
            /* ignore transient race during stop */
        }
    }

    private void OnSysStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && _running)
            _lastError = "Захват наушников остановлен: " + e.Exception.Message;
    }

    private void OnMicStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && _running)
            _lastError = "Захват микрофона остановлен: " + e.Exception.Message;
    }

    private static float ApplyGain(WaveInEventArgs e, WaveFormat fmt, float gain)
    {
        var span = e.Buffer.AsSpan(0, e.BytesRecorded);
        float peak = 0f;

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var samples = MemoryMarshal.Cast<byte, float>(span);
            for (int i = 0; i < samples.Length; i++)
            {
                float v = samples[i] * gain;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                float a = v < 0f ? -v : v;
                if (a > peak) peak = a;
                samples[i] = v;
            }
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            var samples = MemoryMarshal.Cast<byte, short>(span);
            for (int i = 0; i < samples.Length; i++)
            {
                float v = samples[i] / 32768f * gain;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                float a = v < 0f ? -v : v;
                if (a > peak) peak = a;
                samples[i] = (short)(v * 32767f);
            }
        }

        return peak;
    }

    public void Dispose()
    {
        Stop();
    }
}
