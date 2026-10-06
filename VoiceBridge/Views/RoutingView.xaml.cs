using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NAudio.Wave;
using VoiceBridge.Audio;
using VoiceBridge.Services;

namespace VoiceBridge.Views;

public sealed record AppRow(int Pid, string Name, string ProcName, bool Passed);

public partial class RoutingView : UserControl
{
    public AudioEngine Engine { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();

    public event Action? RouteChanged;

    private List<DeviceItem> _renders = new();
    private List<DeviceItem> _captures = new();
    private List<AudioAppItem> _apps = new();
    private int _appPid;
    private bool _updatingUi;
    private bool _initialized;
    private bool? _uiRunning;
    private bool _muted;
    private string? _shownError;
    private string _appListSig = "";
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _appTimer;

    private double _sysShown;
    private double _micShown;
    private double _outShown;

    public DeviceItem? SelectedTarget => CmbTarget.SelectedItem as DeviceItem;

    public DeviceItem? PairedCapture
    {
        get
        {
            var t = SelectedTarget;
            return t is null ? null : AudioDevices.FindPairedCapture(t.Name, _captures);
        }
    }

    public RoutingView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        _appTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _appTimer.Tick += (_, _) =>
        {
            if (_appPid == -1) RefreshAppList();
        };
        _appTimer.Start();
    }

    public void Init(AudioEngine engine, AppSettings settings)
    {
        Engine = engine;
        Settings = settings;
        _appPid = settings.MultiAppMode ? -1 : 0;

        _updatingUi = true;
        SldGain.Value = settings.GainPercent;
        SldMicGain.Value = settings.MicGainPercent;
        TglMicMix.IsChecked = settings.MicMix;
        LblGain.Text = $"{settings.GainPercent}%";
        LblMicGain.Text = $"{settings.MicGainPercent}%";
        _updatingUi = false;

        Engine.SetGains(settings.GainPercent / 100f, settings.MicGainPercent / 100f);
        RefreshDevices();
        UpdateRunUi();
        _initialized = true;
    }

    public void RefreshDevices()
    {
        _updatingUi = true;
        try
        {
            var prevSource = Settings.SourceId ?? (CmbSource.SelectedItem as DeviceItem)?.Id;
            var prevTarget = Settings.TargetId ?? (CmbTarget.SelectedItem as DeviceItem)?.Id;
            var prevMic = Settings.MicId ?? (CmbMic.SelectedItem as DeviceItem)?.Id;

            _renders = AudioDevices.Render();
            _captures = AudioDevices.Capture();

            CmbSource.ItemsSource = _renders;
            CmbTarget.ItemsSource = _renders;
            CmbMic.ItemsSource = _captures;

            CmbSource.SelectedItem =
                _renders.FirstOrDefault(d => d.Id == prevSource)
                ?? _renders.FirstOrDefault(d => d.IsDefault)
                ?? _renders.FirstOrDefault();

            CmbTarget.SelectedItem =
                _renders.FirstOrDefault(d => d.Id == prevTarget)
                ?? AudioDevices.PickBestTarget(_renders, _captures)
                ?? _renders.FirstOrDefault(d => d.IsDefault)
                ?? _renders.FirstOrDefault();

            CmbMic.SelectedItem =
                _captures.FirstOrDefault(d => d.Id == prevMic)
                ?? _captures.FirstOrDefault(d => d.IsDefault)
                ?? _captures.FirstOrDefault();

            RefreshAppsCore();
        }
        finally
        {
            _updatingUi = false;
        }

        SaveRoute();
        UpdateTargetHint();
        UpdateAppHint();
        RouteChanged?.Invoke();
    }

    private void RefreshAppsCore()
    {
        int prev = _appPid;
        string? srcId = (CmbSource.SelectedItem as DeviceItem)?.Id;

        _apps = new List<AudioAppItem>
        {
            new(0, "Вся система — весь звук наушников"),
            new(-1, "По приложениям — передавать звук выборочно")
        };
        _apps.AddRange(AudioApps.GetPlayingApps(srcId));

        CmbApp.ItemsSource = _apps;
        CmbApp.SelectedItem = _apps.FirstOrDefault(a => a.Pid == prev) ?? _apps[0];
        _appPid = (CmbApp.SelectedItem as AudioAppItem)?.Pid ?? 0;
    }

    private void RefreshApps()
    {
        _updatingUi = true;
        try
        {
            RefreshAppsCore();
        }
        finally
        {
            _updatingUi = false;
        }
        UpdateAppHint();
    }

    private void CmbApp_DropDownOpened(object? sender, EventArgs e) => RefreshApps();

    private void BtnRefreshApp_Click(object sender, RoutedEventArgs e) => RefreshApps();

    private void UpdateAppHint()
    {
        if (AppHint is null) return;
        AppHint.Text = _appPid switch
        {
            0 => "Захватывается весь звук выбранного устройства: игры, видео, музыка, браузер.",
            -1 => "Звук пойдёт в Discord только из приложений с галкой ниже — ваш голос через микс передаётся всегда.",
            _ => "В кабель пойдёт только звук этого процесса и его окон — остальное не передаётся."
        };
        if (AppListPanel is not null)
        {
            AppListPanel.Visibility = _appPid == -1 ? Visibility.Visible : Visibility.Collapsed;
            if (_appPid == -1) RefreshAppList();
        }
    }

    private bool IsAppMuted(string proc) =>
        Settings.MutedApps.Any(m => string.Equals(m, proc, StringComparison.OrdinalIgnoreCase));

    /// <summary>Строит список приложений и синхронизирует захват/мут с движком.</summary>
    private void RefreshAppList()
    {
        if (_appPid != -1 || AppChecks is null || Settings is null) return;

        var srcId = (CmbSource.SelectedItem as DeviceItem)?.Id;
        var apps = AudioApps.GetPlayingApps(srcId);

        if (Engine.IsRunning && Engine.MultiAppActive)
        {
            var present = apps.Select(a => a.Pid).ToHashSet();
            foreach (int pid in Engine.ActiveAppPids)
            {
                if (!present.Contains(pid))
                    Engine.RemoveApp(pid);
            }
            foreach (var a in apps)
            {
                try { Engine.AddApp(a.Pid); }
                catch { /* приложение успело закрыться — попробуем в следующий тик */ }
            }
            foreach (var a in apps)
                Engine.SetAppMuted(a.Pid, IsAppMuted(a.ProcessName));
        }

        var rows = apps
            .Select(a => new AppRow(a.Pid, a.DisplayName, a.ProcessName, !IsAppMuted(a.ProcessName)))
            .ToList();

        string sig = string.Join("|", rows.Select(r => $"{r.Pid}:{r.Passed}"));
        if (sig != _appListSig)
        {
            _appListSig = sig;
            AppChecks.ItemsSource = rows;
        }
    }

    private void AppTgl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.DataContext is not AppRow row) return;
        bool passed = tb.IsChecked == true;

        var muted = Settings.MutedApps;
        muted.RemoveAll(m => string.Equals(m, row.ProcName, StringComparison.OrdinalIgnoreCase));
        if (!passed)
            muted.Add(row.ProcName);
        Settings.Save();

        if (Engine.IsRunning && Engine.MultiAppActive)
        {
            foreach (var r in AppChecks.ItemsSource as List<AppRow> ?? new List<AppRow>())
                Engine.SetAppMuted(r.Pid, IsAppMuted(r.ProcName));
        }

        _appListSig = "";
        RefreshAppList();
    }

    private void SaveRoute()
    {
        Settings.SourceId = (CmbSource.SelectedItem as DeviceItem)?.Id;
        Settings.TargetId = (CmbTarget.SelectedItem as DeviceItem)?.Id;
        Settings.MicId = (CmbMic.SelectedItem as DeviceItem)?.Id;
        Settings.MultiAppMode = _appPid == -1;
        Settings.Save();
    }

    private void Route_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        _appPid = (CmbApp.SelectedItem as AudioAppItem)?.Pid ?? 0;
        if (ReferenceEquals(sender, CmbSource))
            RefreshApps();
        SaveRoute();
        UpdateTargetHint();
        UpdateAppHint();
        RouteChanged?.Invoke();
        if (Engine.IsRunning) RestartRoute();
    }

    private void RestartRoute()
    {
        try
        {
            StartEngine();
            HideNotice();
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, error: true);
        }
        UpdateRunUi();
    }

    private void StartEngine()
    {
        List<int>? pids = null;
        if (_appPid == -1)
        {
            pids = AudioApps.GetPlayingApps(Settings.SourceId).Select(a => a.Pid).ToList();
            if (pids.Count == 0)
                throw new InvalidOperationException(
                    "Сейчас ни одно приложение не играет звук — запустите браузер или игру и нажмите «Запустить» ещё раз.");
        }

        Engine.Start(new AudioEngine.StartOptions
        {
            SourceId = Settings.SourceId,
            TargetId = Settings.TargetId,
            MicMix = TglMicMix.IsChecked == true,
            MicId = Settings.MicId,
            SystemGain = (float)SldGain.Value / 100f,
            MicGain = (float)SldMicGain.Value / 100f,
            LatencyMs = Settings.LatencyMs,
            CapturePid = _appPid > 0 ? _appPid : null,
            CapturePids = pids
        });
        _muted = false;
        if (pids is not null) RefreshAppList();
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (Engine.IsRunning)
        {
            Engine.Stop();
            _muted = false;
            HideNotice();
            UpdateRunUi();
            return;
        }

        if (_renders.Count == 0)
        {
            ShowNotice("Нет устройств вывода. Проверьте, что наушники подключены.", error: true);
            return;
        }

        SaveRoute();
        try
        {
            StartEngine();
            _shownError = null;
            HideNotice();
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, error: true);
        }
        UpdateRunUi();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        if (Engine.IsRunning) RestartRoute();
    }

    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        Engine.SetMuted(_muted, false);
        BtnMute.Content = _muted ? "Включить звук" : "Заглушить";
    }

    private void TglMicMix_Click(object sender, RoutedEventArgs e)
    {
        Settings.MicMix = TglMicMix.IsChecked == true;
        Settings.Save();
        if (Engine.IsRunning) RestartRoute();
    }

    private void SldGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || !_initialized) return;
        LblGain.Text = $"{(int)SldGain.Value}%";
        Settings.GainPercent = (int)SldGain.Value;
        Engine.SetGains((float)SldGain.Value / 100f, (float)SldMicGain.Value / 100f);
        Settings.Save();
    }

    private void SldMicGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || !_initialized) return;
        LblMicGain.Text = $"{(int)SldMicGain.Value}%";
        Settings.MicGainPercent = (int)SldMicGain.Value;
        Engine.SetGains((float)SldGain.Value / 100f, (float)SldMicGain.Value / 100f);
        Settings.Save();
    }

    private void UpdateTargetHint()
    {
        var target = SelectedTarget;
        if (target is null)
        {
            TargetHint.Text = "";
            return;
        }

        var paired = AudioDevices.FindPairedCapture(target.Name, _captures);
        if (paired is not null)
        {
            TargetHint.Text = $"В Discord выберите микрофон: «{paired.Name}»";
            TargetHint.Foreground = (System.Windows.Media.Brush)FindResource("AccentAltBrush");
        }
        else
        {
            TargetHint.Text = "⚠ Это не похоже на виртуальный кабель — в списке микрофонов Discord такое устройство не появится.";
            TargetHint.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
        }
    }

    private void UpdateRunUi()
    {
        bool running = Engine.IsRunning;
        _uiRunning = running;
        BtnStart.Content = running ? "⏹   Остановить" : "▶   Запустить";
        BtnMute.IsEnabled = running;
        BtnMute.Content = _muted ? "Включить звук" : "Заглушить";

        if (running)
        {
            StateDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99));
            StateText.Text = "Активно";
            var f = Engine.OutputFormat;
            FmtInfo.Text = f is null
                ? ""
                : $"Формат вывода: {f.SampleRate} Гц • {f.Channels} кан. • {f.Encoding} • буфер {Settings.LatencyMs} мс";
        }
        else
        {
            StateDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x6B, 0x76, 0x90));
            StateText.Text = "Остановлено";
            FmtInfo.Text = "";
            _sysShown = _micShown = _outShown = 0;
            SetWidth(MeterSys, MeterSysTrack, 0);
            SetWidth(MeterMic, MeterMicTrack, 0);
            SetWidth(MeterOut, MeterOutTrack, 0);
            LblSysPeak.Text = "−∞ дБ";
            LblMicPeak.Text = "−∞ дБ";
            LblOutPeak.Text = "−∞ дБ";
        }
    }

    private void Tick()
    {
        bool running = Engine.IsRunning;

        _sysShown = Decay(_sysShown, running ? Engine.SystemPeak : 0f);
        _micShown = Decay(_micShown, running ? Engine.MicPeak : 0f);
        _outShown = Decay(_outShown, running ? Engine.OutputPeak : 0f);

        SetWidth(MeterSys, MeterSysTrack, _sysShown);
        SetWidth(MeterMic, MeterMicTrack, _micShown);
        SetWidth(MeterOut, MeterOutTrack, _outShown);

        LblSysPeak.Text = DbText(running ? Engine.SystemPeak : 0f);
        LblMicPeak.Text = DbText(running ? Engine.MicPeak : 0f);
        LblOutPeak.Text = DbText(running ? Engine.OutputPeak : 0f);

        var err = Engine.LastError;
        if (!string.IsNullOrEmpty(err) && err != _shownError)
        {
            _shownError = err;
            ShowNotice(err, error: true);
        }

        if (Engine.IsRunning != _uiRunning)
            UpdateRunUi();
    }

    private static double Decay(double shown, float peak)
    {
        double target = Math.Sqrt(Math.Clamp(peak, 0f, 1f));
        return target > shown ? target : shown * 0.86;
    }

    private static void SetWidth(System.Windows.FrameworkElement fill,
        System.Windows.FrameworkElement track, double ratio)
    {
        fill.Width = Math.Clamp(ratio, 0, 1) * Math.Max(0, track.ActualWidth);
    }

    private static string DbText(float peak)
    {
        if (peak <= 0.00001f) return "−∞ дБ";
        double db = 20 * Math.Log10(peak);
        return $"{db:0.0} дБ";
    }

    private void ShowNotice(string message, bool error)
    {
        NoticeText.Text = message;
        var color = error ? "DangerBrush" : "WarningBrush";
        var brush = (System.Windows.Media.Brush)FindResource(color);
        Notice.BorderBrush = brush;
        NoticeIcon.Foreground = brush;
        NoticeIcon.Text = error ? "\uE711" : "\uE7BA";
        Notice.Visibility = Visibility.Visible;
    }

    private void HideNotice()
    {
        Notice.Visibility = Visibility.Collapsed;
        if (Engine.LastError is null) _shownError = null;
    }

    internal void BenchStopTimers()
    {
        _timer.Stop();
        _appTimer.Stop();
    }

    internal string TimersState() => $"rt={_timer.IsEnabled} app={_appTimer.IsEnabled}";
}
