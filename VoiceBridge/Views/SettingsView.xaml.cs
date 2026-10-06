using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceBridge.Audio;
using VoiceBridge.Services;

namespace VoiceBridge.Views;

public partial class SettingsView : UserControl
{
    private AppSettings? _s;
    private bool _updating;
    private bool _testing;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceBridge", "selftest.log");

    public event Action<string?, int>? BackgroundChanged;

    public SettingsView()
    {
        InitializeComponent();
    }

    public void Init(AppSettings s)
    {
        _s = s;

        _updating = true;
        TglAutostart.IsChecked = s.Autostart || AppSettings.IsAutostartEnabled();
        TglMinOnStart.IsChecked = s.MinimizedOnStart;
        SldLatency.Value = s.LatencyMs;
        SldDim.Value = s.BgDim;
        _updating = false;

        UpdateLabels();
    }

    private void UpdateLabels()
    {
        LblLatency.Text = $"{(int)SldLatency.Value} мс";
        LblDim.Text = $"{(int)SldDim.Value}%";
    }

    private void SldLatency_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _s is null) return;
        UpdateLabels();
        _s.LatencyMs = (int)SldLatency.Value;
        _s.Save();
    }

    // ================= Проверка =================

    private void SetCheck(string text, string brushKey)
    {
        CheckResult.Text = text;
        CheckResult.Foreground = (Brush)FindResource(brushKey);
    }

    private async void BtnTestCable_Click(object sender, RoutedEventArgs e)
    {
        if (_testing || _s is null) return;
        _testing = true;
        BtnTestCable.IsEnabled = false;
        try
        {
            SetCheck("Отправляю тест-тон в кабель…", "TextSecondaryBrush");
            var id = _s.TargetId;
            var name = AudioDevices.ResolveName(id) ?? "устройство по умолчанию";
            await Task.Run(() => PlayTone(id, 2));
            SetCheck($"✓ Тон отправлен в «{name}» — откройте в Discord микрофон и проверьте индикатор входа.", "SuccessBrush");
        }
        catch (Exception ex)
        {
            SetCheck($"✗ Не получилось: {ex.Message}", "DangerBrush");
        }
        finally
        {
            _testing = false;
            BtnTestCable.IsEnabled = true;
        }
    }

    private async void BtnTestPhones_Click(object sender, RoutedEventArgs e)
    {
        if (_testing || _s is null) return;
        _testing = true;
        BtnTestPhones.IsEnabled = false;
        try
        {
            SetCheck("Играю тон в наушники…", "TextSecondaryBrush");
            var id = _s.SourceId;
            var name = AudioDevices.ResolveName(id) ?? "устройство по умолчанию";
            await Task.Run(() => PlayTone(id, 2));
            SetCheck($"✓ Тон проигран в «{name}» — если слышите сигнал, вывод работает.", "SuccessBrush");
        }
        catch (Exception ex)
        {
            SetCheck($"✗ Не получилось: {ex.Message}", "DangerBrush");
        }
        finally
        {
            _testing = false;
            BtnTestPhones.IsEnabled = true;
        }
    }

    private async void BtnTestMic_Click(object sender, RoutedEventArgs e)
    {
        if (_testing || _s is null) return;
        _testing = true;
        BtnTestMic.IsEnabled = false;
        try
        {
            SetCheck("Слушаю микрофон 2 секунды — говорите или постучите…", "TextSecondaryBrush");
            var id = _s.MicId;
            var name = AudioDevices.ResolveName(id) ?? "микрофон по умолчанию";
            var (peak, packets) = await Task.Run(() => MeasureMic(id, 2));
            if (packets == 0)
                SetCheck($"✗ «{name}»: поток молчит — устройство недоступно или занято другим приложением.", "DangerBrush");
            else if (peak <= 0.00001f)
                SetCheck($"⚠ «{name}»: открывается, но звука нет — проверьте, не выключен ли микрофон аппаратно.", "WarningBrush");
            else
            {
                double db = 20 * Math.Log10(peak);
                SetCheck($"✓ «{name}» работает, пик {db:0.0} дБ.", "SuccessBrush");
            }
        }
        catch (Exception ex)
        {
            SetCheck($"✗ Не получилось: {ex.Message}", "DangerBrush");
        }
        finally
        {
            _testing = false;
            BtnTestMic.IsEnabled = true;
        }
    }

    private async void BtnSelfTest_Click(object sender, RoutedEventArgs e)
    {
        if (_testing) return;
        _testing = true;
        BtnSelfTest.IsEnabled = false;
        try
        {
            SetCheck("Самопроверка выполняется (~20 секунд)…", "TextSecondaryBrush");
            int code = await Task.Run(SelfTest.Run);
            BtnOpenLog.Visibility = Visibility.Visible;
            SetCheck(code == 0
                ? "✓ Самопроверка пройдена — все проверки зелёные. Журнал можно открыть кнопкой ниже."
                : "✗ Самопроверка: есть ошибки — откройте журнал и посмотрите последние строки.",
                code == 0 ? "SuccessBrush" : "DangerBrush");
        }
        catch (Exception ex)
        {
            SetCheck($"✗ Самопроверка упала: {ex.Message}", "DangerBrush");
        }
        finally
        {
            _testing = false;
            BtnSelfTest.IsEnabled = true;
        }
    }

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(LogPath))
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{LogPath}\"") { UseShellExecute = true });
            else
                SetCheck("Журнал ещё не создан — сначала запустите самопроверку.", "WarningBrush");
        }
        catch (Exception ex)
        {
            SetCheck($"Не удалось открыть журнал: {ex.Message}", "DangerBrush");
        }
    }

    private static void PlayTone(string? deviceId, int seconds)
    {
        using var en = new MMDeviceEnumerator();
        MMDevice dev;
        if (!string.IsNullOrEmpty(deviceId))
            dev = en.GetDevice(deviceId);
        else
            dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        var mix = dev.AudioClient.MixFormat;
        var sine = new SineSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels), 440, 0.5f);
        using var outp = new WasapiOut(dev, AudioClientShareMode.Shared, true, 100);
        outp.Init(new SampleToWaveProvider(sine));
        outp.Play();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        outp.Stop();
    }

    private static (float peak, int packets) MeasureMic(string? deviceId, int seconds)
    {
        using var en = new MMDeviceEnumerator();
        MMDevice dev;
        if (!string.IsNullOrEmpty(deviceId))
            dev = en.GetDevice(deviceId);
        else
            dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

        using var cap = new WasapiCapture(dev);
        float peak = 0;
        int packets = 0;
        cap.DataAvailable += (_, e) =>
        {
            packets++;
            if (cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                    e.Buffer.AsSpan(0, e.BytesRecorded));
                for (int i = 0; i < s.Length; i++)
                {
                    float a = Math.Abs(s[i]);
                    if (a > peak) peak = a;
                }
            }
            else if (cap.WaveFormat.Encoding == WaveFormatEncoding.Pcm && cap.WaveFormat.BitsPerSample == 16)
            {
                var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(
                    e.Buffer.AsSpan(0, e.BytesRecorded));
                for (int i = 0; i < s.Length; i++)
                {
                    float a = Math.Abs(s[i] / 32768f);
                    if (a > peak) peak = a;
                }
            }
        };
        cap.StartRecording();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        cap.StopRecording();
        return (peak, packets);
    }

    // ================= Автозапуск =================

    private void TglAutostart_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        bool on = TglAutostart.IsChecked == true;
        _s.Autostart = on;
        AppSettings.ApplyAutostart(on, _s.MinimizedOnStart);
        _s.Save();
    }

    private void TglMinOnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        _s.MinimizedOnStart = TglMinOnStart.IsChecked == true;
        _s.Save();
        if (_s.Autostart || AppSettings.IsAutostartEnabled())
            AppSettings.ApplyAutostart(true, _s.MinimizedOnStart);
    }

    // ================= Фон и сброс =================

    private void SldDim_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _s is null) return;
        UpdateLabels();
        _s.BgDim = (int)SldDim.Value;
        _s.Save();
        BackgroundChanged?.Invoke(_s.BgPath, _s.BgDim);
    }

    private void BtnPickBg_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Выберите изображение фона",
            Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif|Все файлы|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        _s.BgPath = dlg.FileName;
        _s.Save();
        BackgroundChanged?.Invoke(_s.BgPath, _s.BgDim);
    }

    private void BtnResetBg_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        _s.BgPath = null;
        _s.Save();
        BackgroundChanged?.Invoke(null, _s.BgDim);
    }

    private void BtnResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Сбросить все настройки и перезапустить приложение?",
            "Сброс настроек",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        AppSettings.ApplyAutostart(false);
        new AppSettings().Save();

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
