using NAudio.CoreAudioApi;

namespace VoiceBridge.Audio;

public sealed record DeviceItem(string Id, string Name, bool IsDefault, bool IsVirtual)
{
    public string DisplayName => Name + (IsDefault ? "  (по умолчанию)" : "") + (IsVirtual ? "  • виртуальный" : "");
}

public static class AudioDevices
{
    private static readonly string[] VirtualHints =
    {
        "virtual audio cable", "cable input", "cable output", "voice-meeter", "voicemeeter",
        "vb-audio", "voicemod", "magicmic", "itop virtual", "virtual audio device",
        "nvidia virtual audio", "blackhole"
    };

    public static bool LooksVirtual(string name)
    {
        var n = name.ToLowerInvariant();
        return VirtualHints.Any(h => n.Contains(h, StringComparison.Ordinal));
    }

    public static bool LooksLikeCable(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("cable", StringComparison.Ordinal) || n.Contains("voice-meeter", StringComparison.Ordinal)
            || n.Contains("voicemeeter", StringComparison.Ordinal);
    }

    public static List<DeviceItem> Render()
    {
        using var en = new MMDeviceEnumerator();
        string? defId = TryDefaultId(en, DataFlow.Render);
        var list = new List<DeviceItem>();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            list.Add(new DeviceItem(d.ID, d.FriendlyName, d.ID == defId, LooksVirtual(d.FriendlyName)));
        return list;
    }

    public static List<DeviceItem> Capture()
    {
        using var en = new MMDeviceEnumerator();
        string? defId = TryDefaultId(en, DataFlow.Capture);
        var list = new List<DeviceItem>();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            list.Add(new DeviceItem(d.ID, d.FriendlyName, d.ID == defId, LooksVirtual(d.FriendlyName)));
        return list;
    }

    public static string? DefaultRenderId()
    {
        using var en = new MMDeviceEnumerator();
        return TryDefaultId(en, DataFlow.Render);
    }

    public static string? DefaultCaptureId()
    {
        using var en = new MMDeviceEnumerator();
        return TryDefaultId(en, DataFlow.Capture);
    }

    public static string? ResolveName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDevice(id).FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryDefaultId(MMDeviceEnumerator en, DataFlow flow)
    {
        try
        {
            return en.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Имя микрофона, которое нужно выбрать в Discord для выбранного устройства-вывода.
    /// </summary>
    public static DeviceItem? FindPairedCapture(string renderName, List<DeviceItem> captures)
    {
        foreach (var c in captures)
            if (string.Equals(c.Name, renderName, StringComparison.OrdinalIgnoreCase))
                return c;

        if (renderName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
        {
            var want = renderName.Replace("Input", "Output", StringComparison.OrdinalIgnoreCase);
            foreach (var c in captures)
                if (c.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase))
                    return c;
        }

        int open = renderName.LastIndexOf('(');
        if (open > 0 && renderName.EndsWith(')'))
        {
            var token = renderName.Substring(open + 1, renderName.Length - open - 2);
            if (token.Length > 0)
                foreach (var c in captures)
                    if (c.Name.Contains($"({token})", StringComparison.OrdinalIgnoreCase))
                        return c;
        }

        // общий фолбэк «cable» — но не сопоставляем сломанный VAC с чужим кабелем
        if (renderName.Contains("cable", StringComparison.OrdinalIgnoreCase) &&
            !renderName.Contains("virtual audio cable", StringComparison.OrdinalIgnoreCase))
            foreach (var c in captures)
                if (c.Name.Contains("cable", StringComparison.OrdinalIgnoreCase))
                    return c;

        return null;
    }

    /// <summary>Лучший кандидат в цель (виртуальный кабель для Discord-микрофона).</summary>
    public static DeviceItem? PickBestTarget(List<DeviceItem> renders, List<DeviceItem> captures)
    {
        foreach (var r in renders)
            if (r.Name.Contains("vb-audio", StringComparison.OrdinalIgnoreCase) &&
                FindPairedCapture(r.Name, captures) != null)
                return r;
        foreach (var r in renders)
            if (LooksLikeCable(r.Name) && FindPairedCapture(r.Name, captures) != null)
                return r;
        foreach (var r in renders)
            if (LooksVirtual(r.Name) && FindPairedCapture(r.Name, captures) != null)
                return r;
        foreach (var r in renders)
            if (LooksLikeCable(r.Name))
                return r;
        return null;
    }
}
