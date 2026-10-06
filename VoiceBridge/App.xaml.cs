using System.Runtime.InteropServices;
using System.Windows;
using VoiceBridge.Audio;

namespace VoiceBridge;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(AttachParentProcess);
            int code = SelfTest.Run();
            Shutdown(code);
            return;
        }

        int benchIdx = Array.FindIndex(e.Args, a => a.Equals("--bench", StringComparison.OrdinalIgnoreCase));
        if (benchIdx >= 0)
        {
            AttachConsole(AttachParentProcess);
            string mode = benchIdx + 1 < e.Args.Length ? e.Args[benchIdx + 1] : "idle";
            if (mode.StartsWith("gui", StringComparison.Ordinal))
            {
                int sec = 8;
                if (benchIdx + 2 < e.Args.Length) int.TryParse(e.Args[benchIdx + 2], out sec);
                if (mode == "gui-bare")
                {
                    var bare = new Window
                    {
                        Width = 400,
                        Height = 300,
                        Title = "bare",
                        Content = new System.Windows.Controls.Border
                        {
                            Background = System.Windows.Media.Brushes.Black
                        }
                    };
                    bare.Show();
                    SelfTest.StartBareBench(bare, sec);
                    return;
                }
                var benchWin = new MainWindow();
                MainWindow = benchWin;
                if (mode.Contains("-empty", StringComparison.Ordinal))
                    benchWin.Content = new System.Windows.Controls.Border
                    {
                        Background = System.Windows.Media.Brushes.Black
                    };
                if (!mode.Contains("-noshow", StringComparison.Ordinal))
                    benchWin.Show();
                SelfTest.StartGuiBench(mode, sec, benchWin);
                return;
            }
            int code = SelfTest.RunBench(e.Args);
            Shutdown(code);
            return;
        }

        var win = new MainWindow();
        MainWindow = win;
        if (e.Args.Any(a => a.Equals("--min", StringComparison.OrdinalIgnoreCase)))
            win.WindowState = WindowState.Minimized;
        win.Show();
    }
}
