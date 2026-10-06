using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VoiceBridge.Services;

public sealed class AppSettings
{
    public string? SourceId { get; set; }
    public string? TargetId { get; set; }
    public string? MicId { get; set; }
    public int GainPercent { get; set; } = 100;
    public int MicGainPercent { get; set; } = 100;
    public bool MicMix { get; set; } = true;
    public bool MultiAppMode { get; set; }
    public List<string> MutedApps { get; set; } = new();
    public int LatencyMs { get; set; } = 50;
    public bool Autostart { get; set; }
    public bool MinimizedOnStart { get; set; } = true;
    public string? BgPath { get; set; }
    public int BgDim { get; set; } = 55;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceBridge");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings();
        }
        catch
        {
            /* corrupted settings -> defaults */
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            /* best-effort */
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "VoiceBridge";

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(RunValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void ApplyAutostart(bool enable, bool minimized = true)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(RunValueName, $"\"{exe}\"{(minimized ? " --min" : "")}");
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }
        }
        catch
        {
            /* best-effort */
        }
    }
}
