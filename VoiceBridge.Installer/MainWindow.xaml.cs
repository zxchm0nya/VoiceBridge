using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

namespace VoiceBridge.Installer;

public partial class MainWindow : Window
{
    private const string AppExeResource = "VoiceBridge.App.exe";
    private const string AppExeName = "VoiceBridge.exe";
    private const string SetupExeName = "VoiceBridgeSetup.exe";
    private bool _silent;
    private string _logFile = "";

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VoiceBridge";
    private const string AppKey = @"SOFTWARE\VoiceBridge";
    private const string InstallerTitle = "VoiceBridge Setup";

    private int _step = 1;
    private bool _isUninstalling;
    private bool _isUpdate;
    private string _existingInstall = "";
    private bool _cableMissing;
    private bool _installCable;

    private const string CableUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip";

    public MainWindow()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs();
        _isUninstalling = args.Contains("--uninstall");

        // Existing install in the registry means this setup updates in place.
        var prev = GetRegistryValue(AppKey, "InstallPath") as string;
        if (!string.IsNullOrEmpty(prev) && File.Exists(Path.Combine(prev, AppExeName)))
        {
            _isUpdate = true;
            _existingInstall = prev;
        }

        if (_isUninstalling)
        {
            Title = "VoiceBridge Uninstaller";
            SetupUninstallUi();
            if (args.Contains("--silent"))
            {
                _silent = true;
                _logFile = Path.Combine(Path.GetTempPath(), "voicebridge-install.log");
                Log("VoiceBridge silent uninstall started");
                Loaded += async (_, _) =>
                {
                    Hide();
                    ShowStep(4);
                    await RunUninstall();
                    Close();
                };
            }
        }
        else if (args.Contains("--silent"))
        {
            _silent = true;
            _logFile = Path.Combine(Path.GetTempPath(), "voicebridge-install.log");
            Log("VoiceBridge silent install started");
            Loaded += async (_, _) =>
            {
                var envPath = Environment.GetEnvironmentVariable("VB_INSTALL_DIR");
                InstallPath.Text = !string.IsNullOrEmpty(envPath)
                    ? envPath
                    : _isUpdate
                        ? _existingInstall
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VoiceBridge");
                Hide();
                await RunInstall();
                Close();
            };
        }
        else if (_isUpdate)
        {
            InstallPath.Text = _existingInstall;
            StepWelcomeText.Text = "This wizard will update VoiceBridge on your computer. "
                + "Existing settings are kept, only files are replaced.";
        }

        ShowStep(1);
    }

    /// <summary>Kills a running VoiceBridge so its exe can be overwritten.</summary>
    private static async Task StopRunningApp()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
                p.Kill();
            await Task.Delay(300);
        }
        catch { }
    }

    /// <summary>
    /// Deletes the install folder. When this setup was copied there (UninstallString),
    /// its own exe is locked, so the rest is removed now and the leftover folder
    /// is scheduled for deletion after we exit.
    /// </summary>
    private void RemoveInstallFolder(string target)
    {
        string self = Path.GetFullPath(Environment.ProcessPath ?? "");
        string full = Path.GetFullPath(target);

        bool selfInside = self.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!selfInside)
        {
            Directory.Delete(target, true);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(target))
        {
            try
            {
                if (Path.GetFullPath(entry).Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
            catch (Exception ex) { Log("Leftover delete failed: " + ex.Message); }
        }

        // cmd waits for this process to exit, then removes the folder with our exe in it.
        try
        {
            var psi = new ProcessStartInfo("cmd.exe",
                $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{full}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            Process.Start(psi);
        }
        catch (Exception ex) { Log("Deferred folder delete failed: " + ex.Message); }
    }

    private void ShowStep(int step)
    {
        _step = step;
        StepWelcome.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepPath.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepDeps.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepProgress.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        StepDone.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;

        BtnBack.Visibility = step is 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
        BtnCancel.Visibility = step <= 3 ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.Visibility = step != 4 ? Visibility.Visible : Visibility.Collapsed;

        BtnNext.Content = _isUninstalling
            ? (step == 1 ? "Uninstall" : step == 5 ? "Finish" : "Next")
            : (step == 1 ? "Next"
                : step == 2 ? (_isUpdate ? "Update" : "Install")
                : step == 3 ? "Continue"
                : step == 5 ? "Finish" : "Next");
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_isUninstalling)
        {
            if (_step == 1) { ShowStep(4); _ = RunUninstall(); }
            else if (_step == 5) { Close(); }
            return;
        }

        switch (_step)
        {
            case 1:
                ShowStep(2);
                break;
            case 2:
                // Virtual cable missing -> ask about the dependency before installing.
                _cableMissing = !IsVirtualCableInstalled();
                if (_cableMissing) ShowStep(3);
                else ShowStep(4);
                if (_step == 4) _ = RunInstall();
                break;
            case 3:
                _installCable = ChkInstallCable.IsChecked == true;
                ShowStep(4);
                _ = RunInstall();
                break;
            case 5:
                if (LaunchAfter.IsChecked == true) LaunchApp();
                Close();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 2) ShowStep(1);
        else if (_step == 3) ShowStep(2);
    }

    private void CableChk_Changed(object sender, RoutedEventArgs e)
    {
        // Fires from XAML while the panel is still being parsed.
        if (DepsWarning == null) return;
        DepsWarning.Visibility = ChkInstallCable.IsChecked == true
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select install folder",
            SelectedPath = InstallPath.Text,
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            InstallPath.Text = dlg.SelectedPath;
    }

    private async Task RunInstall()
    {
        BtnNext.IsEnabled = false;

        string target = InstallPath.Text.Trim();
        string exeTarget = Path.Combine(target, AppExeName);
        bool updating = File.Exists(exeTarget);

        ProgressText.Text = "Checking dependencies...";
        InstallProgress.Value = 5;

        try
        {
            // Interactive flow sets _cableMissing at step 2; silent mode never asked.
            if (_silent)
            {
                _cableMissing = !IsVirtualCableInstalled();
                // VB-Cable has no silent mode: never pop its window from --silent.
                if (_cableMissing)
                    Log("Warning: no virtual audio cable found. Install VB-Cable manually, VoiceBridge cannot work without it.");
            }
            if (_cableMissing && _installCable)
                await InstallVirtualCable();

            ProgressText.Text = updating ? "Checking existing installation..." : "Checking installation folder...";
            InstallProgress.Value = 10;
            Directory.CreateDirectory(target);

            if (updating)
            {
                // A running copy locks its exe: overwrite would silently fail.
                ProgressText.Text = "Stopping running VoiceBridge...";
                await StopRunningApp();
            }

            ProgressText.Text = updating ? "Updating VoiceBridge..." : "Extracting VoiceBridge...";
            InstallProgress.Value = 35;
            await Task.Delay(100);

            byte[]? data = ExtractResource(AppExeResource);
            if (data == null)
                throw new InvalidOperationException("Bundled application data is missing.");

            // OverwriteFiles semantics: updating over the old version just replaces the file.
            await File.WriteAllBytesAsync(exeTarget, data);
            InstallProgress.Value = 50;

            ProgressText.Text = "Creating shortcuts...";
            await Task.Delay(80);

            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var startMenuDir = Path.Combine(startMenu, "VoiceBridge");
            Directory.CreateDirectory(startMenuDir);

            CreateShortcut(Path.Combine(startMenuDir, "VoiceBridge.lnk"), exeTarget);
            if (ChkDesktopShortcut.IsChecked == true)
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "VoiceBridge.lnk"), exeTarget);

            ProgressText.Text = "Registering uninstaller...";
            await Task.Delay(80);
            WriteRegistry(exeTarget);

            // UninstallString points at VoiceBridgeSetup.exe inside the install
            // folder, so the setup must be copied there (skip when run from it).
            try
            {
                string self = Environment.ProcessPath ?? "";
                string setupCopy = Path.Combine(target, SetupExeName);
                if (!string.IsNullOrEmpty(self)
                    && !string.Equals(Path.GetFullPath(self), Path.GetFullPath(setupCopy), StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, setupCopy, true);
            }
            catch (Exception ex) { Log("Setup copy failed: " + ex.Message); }

            InstallProgress.Value = 100;
            ProgressText.Text = "Done.";

            DoneSub.Text = updating
                ? $"VoiceBridge was updated to the latest version:\n{target}"
                : $"VoiceBridge was installed to:\n{target}";
            if (_cableMissing)
                DoneSub.Text += "\n\nWarning: no virtual audio cable was found. VoiceBridge cannot work without it and will show a warning at every launch.";
            ShowStep(5);
        }
        catch (Exception ex)
        {
            InstallProgress.Value = 0;
            ProgressText.Text = "Installation failed: " + ex.Message;
            Log("Install failed: " + ex);
            if (_silent)
            {
                try { File.WriteAllText(Path.Combine(InstallPath.Text, "install-error.txt"), ex.ToString()); } catch { }
                Close();
                return;
            }
            MessageBox.Show("Installation failed.\n\n" + ex.Message, InstallerTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            ShowStep(2);
        }
        finally
        {
            BtnNext.IsEnabled = true;
        }
    }

    private async Task RunUninstall()
    {
        BtnNext.IsEnabled = false;
        ProgressText.Text = "Removing program files...";
        InstallProgress.Value = 40;

        try
        {
            string? target = GetRegistryValue(AppKey, "InstallPath") as string;
            if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
            {
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
                    p.Kill();

                await Task.Delay(150);
                RemoveInstallFolder(target);
            }
            InstallProgress.Value = 70;

            var startMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "VoiceBridge", "VoiceBridge.lnk");
            var desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "VoiceBridge.lnk");
            if (File.Exists(startMenuLink)) File.Delete(startMenuLink);
            if (File.Exists(desktopLink)) File.Delete(desktopLink);
            var smDir = Path.GetDirectoryName(startMenuLink);
            if (!string.IsNullOrEmpty(smDir) && Directory.Exists(smDir) && !Directory.EnumerateFileSystemEntries(smDir).Any())
                Directory.Delete(smDir);

            InstallProgress.Value = 90;

            using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
                key?.DeleteSubKeyTree("", false);
            using (var key = Registry.LocalMachine.CreateSubKey(AppKey))
                key?.DeleteSubKeyTree("", false);

            InstallProgress.Value = 100;
            DoneSub.Text = "VoiceBridge was removed from your computer.";
            ShowStep(5);
        }
        catch (Exception ex)
        {
            ProgressText.Text = "Uninstall failed: " + ex.Message;
            Log("Uninstall failed: " + ex);
            if (_silent) { Close(); return; }
            MessageBox.Show("Uninstall failed.\n\n" + ex.Message, InstallerTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            ShowStep(1);
        }
        finally
        {
            BtnNext.IsEnabled = true;
        }
    }

    private void SetupUninstallUi()
    {
        BtnNext.Content = "Uninstall";
        ProgressText.Text = "Removing VoiceBridge...";
        StepWelcome.Children.Clear();
        StepWelcome.Children.Add(new TextBlock
        {
            Text = "Uninstall VoiceBridge?",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        StepWelcome.Children.Add(new TextBlock
        {
            Text = "This will remove the application, shortcuts and registry entries from your computer.",
            TextWrapping = TextWrapping.Wrap
        });
        StepPath.Visibility = Visibility.Collapsed;
        ShowStep(1);
    }

    private void Log(string msg)
    {
        try { File.AppendAllText(_logFile, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}"); } catch { }
    }

    /// <summary>True when a virtual audio cable (VB-Cable or any cable-like device) exists.</summary>
    private static bool IsVirtualCableInstalled()
    {
        try
        {
            // VB-Cable drops a kernel driver into system32\drivers.
            var drivers = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers");
            if (Directory.Exists(drivers)
                && Directory.GetFiles(drivers, "vbaudio*.sys").Length > 0)
                return true;

            // VB-Cable registers itself under Uninstall.
            using var un = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (un != null)
            {
                foreach (var name in un.GetSubKeyNames())
                {
                    if (name.StartsWith("VB:VBCABLE", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Downloads the official VB-Cable pack and runs its setup. The pack has no
    /// silent mode, so its own window is shown and we wait for the user to finish.
    /// </summary>
    private async Task InstallVirtualCable()
    {
        ProgressText.Text = "Downloading VB-Cable installer...";
        InstallProgress.Value = 15;
        Log("Downloading " + CableUrl);

        string dir = Path.Combine(Path.GetTempPath(), "voicebridge-vbcable");
        string zip = Path.Combine(dir, "VBCABLE_Driver_Pack43.zip");
        Directory.CreateDirectory(dir);

        using (var http = new HttpClient())
        {
            using var resp = await http.GetAsync(CableUrl);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(zip, bytes);
        }

        InstallProgress.Value = 30;
        ProgressText.Text = "Extracting VB-Cable installer...";
        ZipFile.ExtractToDirectory(zip, dir, true);

        string setup = Path.Combine(dir,
            Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe");
        if (!File.Exists(setup))
            throw new FileNotFoundException("VB-Cable setup executable not found.", setup);

        ProgressText.Text = "Running VB-Cable setup - complete it in its window...";
        Log("Running " + setup);
        var psi = new ProcessStartInfo(setup) { UseShellExecute = true, WorkingDirectory = dir };
        using var proc = Process.Start(psi);
        if (proc != null)
            await proc.WaitForExitAsync();

        InstallProgress.Value = 45;
        if (IsVirtualCableInstalled())
        {
            Log("VB-Cable installed");
            _cableMissing = false;
        }
        else
        {
            Log("VB-Cable setup finished but no cable detected (reboot may be required)");
            _cableMissing = true;
        }
    }

    private static byte[]? ExtractResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        if (res == null) return null;
        using var stream = asm.GetManifestResourceStream(res);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void CreateShortcut(string lnkPath, string exePath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic? wsh = Activator.CreateInstance(shellType);
            if (wsh == null) return;
            dynamic sc = wsh.CreateShortcut(lnkPath);
            sc.TargetPath = exePath;
            sc.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
            sc.IconLocation = exePath + ",0";
            sc.Description = "VoiceBridge - route system audio into a virtual microphone";
            sc.Save();
        }
        catch { }
    }

    private static void LaunchApp()
    {
        try
        {
            var target = Path.Combine(GetRegistryValue(AppKey, "InstallPath") as string ?? "", AppExeName);
            if (File.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { }
    }

    private void WriteRegistry(string exePath)
    {
        try
        {
            using (var k = Registry.LocalMachine.CreateSubKey(AppKey))
            {
                k.SetValue("InstallPath", Path.GetDirectoryName(exePath) ?? "");
                k.SetValue("Version", "1.0.0");
            }

            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "VoiceBridge");
                k.SetValue("DisplayVersion", "1.0.0");
                k.SetValue("Publisher", "VoiceBridge");
                k.SetValue("DisplayIcon", exePath);
                k.SetValue("InstallLocation", Path.GetDirectoryName(exePath) ?? "");
                k.SetValue("UninstallString", $"\"{Path.Combine(Path.GetDirectoryName(exePath) ?? "", "VoiceBridgeSetup.exe")}\" --uninstall");
            }
        }
        catch (Exception ex)
        {
            Log("Registry write failed: " + ex.Message);
            if (!_silent)
                MessageBox.Show("Files were installed, but writing the registry uninstaller entry failed.\n\n" + ex.Message,
                    InstallerTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static object? GetRegistryValue(string subKey, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(subKey);
        return k?.GetValue(name);
    }
}
