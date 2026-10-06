using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using VoiceBridge.Audio;
using VoiceBridge.Services;

namespace VoiceBridge;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AudioEngine _engine = new();
    private readonly DispatcherTimer _footerTimer;
    private DispatcherTimer? _toastTimer;

    internal AudioEngine Engine => _engine;

    internal void BenchStopTimers()
    {
        _footerTimer.Stop();
        PageRouting.BenchStopTimers();
    }

    internal string TimersState() =>
        $"footer={_footerTimer.IsEnabled} {PageRouting.TimersState()}";

    public MainWindow()
    {
        InitializeComponent();

        PageRouting.RouteChanged += UpdateBadge;
        PageRouting.Init(_engine, _settings);
        PageSettings.Init(_settings);
        PageSettings.BackgroundChanged += ApplyBackground;

        ApplyBackground(_settings.BgPath, _settings.BgDim);
        UpdateBadge();
        UpdateFooter();

        _footerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _footerTimer.Tick += (_, _) => UpdateFooter();
        _footerTimer.Start();

        Closed += (_, _) =>
        {
            _footerTimer.Stop();
            _toastTimer?.Stop();
            _engine.Dispose();
            _settings.Save();
        };

        if (!Environment.GetCommandLineArgs()
                .Any(a => a.StartsWith("--bench", StringComparison.OrdinalIgnoreCase)
                       || a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            UpdateChecker.Checked += OnUpdateChecked;
            _ = Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(1200);
                await UpdateChecker.CheckInBackgroundAsync();
            }, DispatcherPriority.Background);
        }
    }

    private void OnUpdateChecked(UpdateInfo? info)
    {
        if (info is { IsNewer: true })
            ShowUpdateToast(info);
    }

    private void ShowUpdateToast(UpdateInfo info)
    {
        UpdateToastTitle.Text = $"Доступно обновление {info.Version}";
        var notes = UpdateChecker.PlainNotes(info.Body, 500);
        if (string.IsNullOrWhiteSpace(notes))
        {
            UpdateToastNotesCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateToastNotesCard.Visibility = Visibility.Visible;
            UpdateToastNotes.Text = notes;
        }

        UpdateToast.Visibility = Visibility.Visible;
        UpdateToast.Opacity = 0;
        UpdateToast.RenderTransform = new TranslateTransform(0, -12);
        UpdateToast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        var slide = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        UpdateToast.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slide);

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(18) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideUpdateToast();
        };
        _toastTimer.Start();
    }

    private void HideUpdateToast()
    {
        if (UpdateToast.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => UpdateToast.Visibility = Visibility.Collapsed;
        UpdateToast.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateToastClose_Click(object sender, RoutedEventArgs e)
    {
        _toastTimer?.Stop();
        HideUpdateToast();
    }

    private void UpdateToastUpdate_Click(object sender, RoutedEventArgs e)
    {
        _toastTimer?.Stop();
        HideUpdateToast();
        UpdateChecker.OpenRelease(UpdateChecker.LastResult);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // чёрная заголовочная полоса (название, свернуть, закрыть)
        var hwnd = new WindowInteropHelper(this).Handle;
        int useDark = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
    }

    private void UpdateBadge()
    {
        var paired = PageRouting.PairedCapture;
        DiscordBadge.Text = paired is not null
            ? $"Микрофон Discord: {paired.Name}"
            : "Микрофон Discord: —";
    }

    private void UpdateFooter()
    {
        if (_engine.IsRunning)
        {
            FooterDot.Fill = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            FooterStatus.Text = "Активно";
        }
        else
        {
            FooterDot.Fill = new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x90));
            FooterStatus.Text = "Остановлено";
        }
    }

    private void ApplyBackground(string? path, int dim)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                BgImageBrush.ImageSource = bmp;
                BgImageHost.Visibility = Visibility.Visible;
                BgDimHost.Opacity = Math.Clamp(dim, 0, 100) / 100.0 * 0.8;
                return;
            }
            catch
            {
                /* broken image -> fallback to gradient */
            }
        }
        BgImageHost.Visibility = Visibility.Collapsed;
        BgDimHost.Opacity = 0;
    }
}
