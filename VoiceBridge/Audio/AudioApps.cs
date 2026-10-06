using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VoiceBridge.Audio;

/// <summary>Приложение (процесс с окном), звук которого можно захватывать отдельно.</summary>
public sealed record AudioAppItem(int Pid, string DisplayName, string ProcessName = "");

public static class AudioApps
{
    /// <summary>
    /// Процессы, у которых есть аудиосессии на указанном устройстве вывода
    /// (или на устройстве по умолчанию, если id не задан).
    /// </summary>
    public static List<AudioAppItem> GetPlayingApps(string? renderDeviceId)
    {
        var result = new List<AudioAppItem>();
        try
        {
            using var en = new MMDeviceEnumerator();
            MMDevice device;
            try
            {
                device = string.IsNullOrEmpty(renderDeviceId)
                    ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : en.GetDevice(renderDeviceId);
            }
            catch
            {
                return result;
            }

            using (device)
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                var seen = new HashSet<int>();

                for (int i = 0; i < sessions.Count; i++)
                {
                    AudioSessionControl? session = null;
                    try
                    {
                        session = sessions[i];
                        if (session.State == AudioSessionState.AudioSessionStateExpired)
                            continue;
                        uint pid = session.GetProcessID;
                        if (pid == 0 || !seen.Add((int)pid))
                            continue;
                        var (display, proc) = Describe((int)pid, session.DisplayName);
                        result.Add(new AudioAppItem((int)pid, display, proc));
                    }
                    catch
                    {
                        /* сломанная сессия — пропускаем */
                    }
                    finally
                    {
                        session?.Dispose();
                    }
                }
            }
        }
        catch
        {
            /* аудиоподсистема недоступна — вернём пустой список */
        }

        result.Sort((x, y) => string.Compare(x.DisplayName, y.DisplayName,
            StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    private static (string Display, string ProcessName) Describe(int pid, string sessionName)
    {
        string processName;
        string windowTitle = "";
        try
        {
            using var p = Process.GetProcessById(pid);
            processName = p.ProcessName;
            windowTitle = p.MainWindowTitle;
        }
        catch
        {
            processName = string.IsNullOrWhiteSpace(sessionName) ? $"процесс {pid}" : sessionName;
        }

        if (!string.IsNullOrWhiteSpace(windowTitle))
            return ($"{processName} — {Truncate(windowTitle, 44)}", processName);
        return ($"{processName} (pid {pid})", processName);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
