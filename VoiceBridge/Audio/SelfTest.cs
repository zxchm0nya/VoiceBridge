using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceBridge.Audio;

/// <summary>РЎР°РјРѕРїСЂРѕРІРµСЂРєР°: Р·Р°С…РІР°С‚ СЃ РЅР°СѓС€РЅРёРєРѕРІ + СЃРєРІРѕР·РЅРѕР№ С‚РµСЃС‚ В«РІС‹РІРѕРґ в†’ РІРёСЂС‚СѓР°Р»СЊРЅС‹Р№ РјРёРєСЂРѕС„РѕРЅВ».</summary>
public static class SelfTest
{
    public static int Run()
    {
        var log = new List<string>();
        bool pass = true;

        void Log(string line)
        {
            log.Add(line);
            Console.WriteLine(line);
        }

        Log($"VoiceBridge selftest  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"NAudio: {typeof(WaveFormat).Assembly.GetName().Version}");
        Log(new string('-', 56));

        try
        {
            var renders = AudioDevices.Render();
            var captures = AudioDevices.Capture();
            Log($"РЈСЃС‚СЂРѕР№СЃС‚РІ РІС‹РІРѕРґР° (active): {renders.Count}, РІРІРѕРґР° (active): {captures.Count}");

            using (var en = new MMDeviceEnumerator())
            {
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
                {
                    string name;
                    try { name = d.FriendlyName; }
                    catch (Exception ex) { name = $"<РёРјСЏ РЅРµРґРѕСЃС‚СѓРїРЅРѕ: 0x{ex.HResult:X8}>"; }
                    Log($"  [all] {d.DataFlow,-7} | {d.State,-8} | {name}");
                }
            }

            // --- РўРµСЃС‚ 1: loopback СЃ СѓСЃС‚СЂРѕР№СЃС‚РІР° РїРѕ СѓРјРѕР»С‡Р°РЅРёСЋ ---
            Log(new string('-', 56));
            int packets = 0;
            float peak = 0f;
            try
            {
                using var en = new MMDeviceEnumerator();
                var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                using var cap = new WasapiLoopbackCapture(dev);
                cap.DataAvailable += (_, e) =>
                {
                    packets++;
                    var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
                    for (int i = 0; i < samples.Length; i++)
                    {
                        float a = Math.Abs(samples[i]);
                        if (a > peak) peak = a;
                    }
                };
                cap.StartRecording();
                Thread.Sleep(1500);
                cap.StopRecording();
                Log($"РўРµСЃС‚ 1 (loopback СЃ В«{dev.FriendlyName}В»): РїР°РєРµС‚РѕРІ={packets}, РїРёРє={peak:F4}, fmt={cap.WaveFormat}");
                if (packets == 0)
                    Log("  ! РїРѕС‚РѕРє loopback РјРѕР»С‡РёС‚ (РЅРµС‚ Р°РєС‚РёРІРЅРѕРіРѕ РІРѕСЃРїСЂРѕРёР·РІРµРґРµРЅРёСЏ) вЂ” РЅРµ РѕС€РёР±РєР°");
            }
            catch (Exception ex)
            {
                Log($"  РўРµСЃС‚ 1 РћРЁРР‘РљРђ: {ex.Message}");
                pass = false;
            }

            // --- РўРµСЃС‚ 2: СЃРёРЅСѓСЃ в†’ РІРёСЂС‚СѓР°Р»СЊРЅС‹Р№ РєР°Р±РµР»СЊ в†’ РµРіРѕ РјРёРєСЂРѕС„РѕРЅРЅР°СЏ СЃС‚РѕСЂРѕРЅР° ---
            Log(new string('-', 56));
            var target = AudioDevices.PickBestTarget(renders, captures);
            if (target is null)
            {
                Log("РўРµСЃС‚ 2 SKIP: РІРёСЂС‚СѓР°Р»СЊРЅС‹Р№ РєР°Р±РµР»СЊ РЅРµ РЅР°Р№РґРµРЅ СЃСЂРµРґРё Р°РєС‚РёРІРЅС‹С… СѓСЃС‚СЂРѕР№СЃС‚РІ РІС‹РІРѕРґР°");
            }
            else
            {
                try
                {
                    using var en = new MMDeviceEnumerator();
                    var renderDev = en.GetDevice(target.Id);

                    var mix = renderDev.AudioClient.MixFormat;
                    var sineFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
                    var sine = new SineSampleProvider(sineFormat, 440, 0.5f);
                    using var outStream = new WasapiOut(renderDev, AudioClientShareMode.Shared, true, 50);
                    outStream.Init(mix.Encoding == WaveFormatEncoding.Pcm && mix.BitsPerSample == 16
                        ? (IWaveProvider)new SampleToWaveProvider16(sine)
                        : new SampleToWaveProvider(sine));
                    Log($"  С„РѕСЂРјР°С‚ РєР°Р±РµР»СЏ: {mix}");

                    Log($"РўРµСЃС‚ 2: РІРєР»СЋС‡Р°СЋ СЃРёРЅСѓСЃ РІ В«{target.Name}В»...");
                    outStream.Play();
                    Thread.Sleep(1200);

                    var freshCaptures = AudioDevices.Capture();
                    Log($"  Р°РєС‚РёРІРЅС‹С… РІС…РѕРґРѕРІ РїРѕСЃР»Рµ РѕС‚РєСЂС‹С‚РёСЏ РІС‹РІРѕРґР°: {freshCaptures.Count} ({string.Join(", ", freshCaptures.Select(c => c.Name))})");

                    var paired = AudioDevices.FindPairedCapture(target.Name, freshCaptures);
                    if (paired is null)
                    {
                        // endpoint РЅРµ РІ Active вЂ” РїРµСЂРµР±РёСЂР°РµРј РІСЃРµ РєР°РЅРґРёРґР°С‚С‹ СЃ РёРјРµРЅРµРј РєР°Р±РµР»СЏ
                        var candidates = new List<(MMDevice dev, string name, DeviceState state)>();
                        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All))
                        {
                            string n;
                            try { n = d.FriendlyName; } catch { continue; }
                            if (n.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                                candidates.Add((d, n, d.State));
                        }

                        if (candidates.Count == 0)
                        {
                            Log($"  РїР°СЂР° РґР»СЏ В«{target.Name}В» РЅРµ РЅР°Р№РґРµРЅР° РІРѕРѕР±С‰Рµ");
                            outStream.Stop();
                            Log("РўРµСЃС‚ 2 FAIL: РЅРµС‚ РјРёРєСЂРѕС„РѕРЅР° РґР»СЏ Discord");
                            pass = false;
                        }
                        else
                        {
                            bool anyPass = false;
                            foreach (var (dev, name, state) in candidates)
                            {
                                Log($"  РїСЂРѕР±СѓСЋ В«{name}В» (state={state})...");
                                try
                                {
                                    float altPeak = 0;
                                    int altPackets = 0;
                                    using var cap2 = new WasapiCapture(dev);
                                    cap2.DataAvailable += (_, e2) =>
                                    {
                                        altPackets++;
                                        if (cap2.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                                        {
                                            var s2 = MemoryMarshal.Cast<byte, float>(e2.Buffer.AsSpan(0, e2.BytesRecorded));
                                            for (int i = 0; i < s2.Length; i++)
                                            {
                                                float a = Math.Abs(s2[i]);
                                                if (a > altPeak) altPeak = a;
                                            }
                                        }
                                        else if (cap2.WaveFormat.Encoding == WaveFormatEncoding.Pcm &&
                                                 cap2.WaveFormat.BitsPerSample == 16)
                                        {
                                            var s2 = MemoryMarshal.Cast<byte, short>(e2.Buffer.AsSpan(0, e2.BytesRecorded));
                                            for (int i = 0; i < s2.Length; i++)
                                            {
                                                float a = Math.Abs(s2[i] / 32768f);
                                                if (a > altPeak) altPeak = a;
                                            }
                                        }
                                    };
                                    cap2.StartRecording();
                                    Thread.Sleep(1500);
                                    cap2.StopRecording();
                                    Log($"    РїР°РєРµС‚РѕРІ={altPackets}, РїРёРє={altPeak:F4}");
                                    if (altPeak > 0.02f && altPackets > 0)
                                    {
                                        anyPass = true;
                                        Log($"  РўРµСЃС‚ 2 PASS: Р·РІСѓРє СЂРµР°Р»СЊРЅРѕ РёРґС‘С‚ РІ В«{name}В» вЂ” РІ Discord РІС‹Р±РёСЂР°Р№С‚Рµ СЌС‚РѕС‚ РјРёРєСЂРѕС„РѕРЅ");
                                        break;
                                    }
                                }
                                catch (Exception ex2)
                                {
                                    Log($"    РЅРµ РѕС‚РєСЂС‹Р»РѕСЃСЊ: {ex2.Message}");
                                }
                            }

                            outStream.Stop();
                            if (!anyPass)
                            {
                                Log("  РўРµСЃС‚ 2 FAIL: РЅРё РѕРґРёРЅ endpoint РєР°Р±РµР»СЏ РЅРµ РїСЂРёРЅРёРјР°РµС‚ Р·РІСѓРє");
                                pass = false;
                            }
                        }
                    }
                    else
                    {
                        float outPeak = 0f;
                        int outPackets = 0;
                        var captureDev = en.GetDevice(paired.Id);
                        using var capture = new WasapiCapture(captureDev);
                        capture.DataAvailable += (_, e) =>
                        {
                            outPackets++;
                            if (capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                            {
                                var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
                                for (int i = 0; i < samples.Length; i++)
                                {
                                    float a = Math.Abs(samples[i]);
                                    if (a > outPeak) outPeak = a;
                                }
                            }
                            else if (capture.WaveFormat.Encoding == WaveFormatEncoding.Pcm &&
                                     capture.WaveFormat.BitsPerSample == 16)
                            {
                                var samples = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
                                for (int i = 0; i < samples.Length; i++)
                                {
                                    float a = Math.Abs(samples[i] / 32768f);
                                    if (a > outPeak) outPeak = a;
                                }
                            }
                        };

                        capture.StartRecording();
                        Thread.Sleep(2000);
                        capture.StopRecording();
                        outStream.Stop();

                        Log($"РўРµСЃС‚ 2 (В«{target.Name}В» в†’ В«{paired.Name}В»): РїР°РєРµС‚РѕРІ={outPackets}, РїРёРє={outPeak:F4}");
                        if (outPeak > 0.02f && outPackets > 0)
                            Log("  РўРµСЃС‚ 2 PASS: РІРёСЂС‚СѓР°Р»СЊРЅС‹Р№ РјРёРєСЂРѕС„РѕРЅ СЂРµР°Р»СЊРЅРѕ РїРѕР»СѓС‡Р°РµС‚ Р·РІСѓРє");
                        else
                        {
                            Log("  РўРµСЃС‚ 2 FAIL: РЅР° СЃС‚РѕСЂРѕРЅРµ РјРёРєСЂРѕС„РѕРЅР° С‚РёС€РёРЅР°");
                            pass = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"  РўРµСЃС‚ 2 РћРЁРР‘РљРђ: {ex}");
                    pass = false;
                }
            }
            // --- РўРµСЃС‚ 3: Р·Р°С…РІР°С‚ Р·РІСѓРєР° РѕРґРЅРѕРіРѕ РїСЂРѕС†РµСЃСЃР° (process loopback) ---
            Log(new string('-', 56));
            try
            {
                int myPid = Environment.ProcessId;
                using var procCap = new ProcessLoopbackCapture(myPid);
                int procPackets = 0;
                procCap.DataAvailable += (_, _) => procPackets++;
                procCap.StartRecording();
                Thread.Sleep(800);
                procCap.StopRecording();
                Log($"РўРµСЃС‚ 3 (process loopback, pid={myPid}): РїР°РєРµС‚РѕРІ={procPackets}, fmt={procCap.WaveFormat}");
                Log("  РўРµСЃС‚ 3 PASS: Р°РєС‚РёРІР°С†РёСЏ РїСЂРѕС†РµСЃСЃРЅРѕРіРѕ Р·Р°С…РІР°С‚Р° СЂР°Р±РѕС‚Р°РµС‚ (РїР°РєРµС‚РѕРІ 0 вЂ” РЅРѕСЂРјР°, РїСЂРѕС†РµСЃСЃ РјРѕР»С‡РёС‚)");
            }
            catch (Exception ex)
            {
                Log($"  РўРµСЃС‚ 3 РћРЁРР‘РљРђ (РїСЂРѕС†РµСЃСЃРЅС‹Р№ Р·Р°С…РІР°С‚ РЅРµРґРѕСЃС‚СѓРїРµРЅ?): {ex}");
                pass = false;
            }

            // --- РўРµСЃС‚ 4: Р¶РёРІРѕР№ Р·Р°РїСѓСЃРє РґРІРёР¶РєР° СЃ РјРёРєС€РµСЂРѕРј (СЃРёСЃС‚РµРјР° + РјРёРєСЂРѕС„РѕРЅ) ---
            Log(new string('-', 56));
            if (target is null)
            {
                Log("РўРµСЃС‚ 4 SKIP: РЅРµС‚ РІРёСЂС‚СѓР°Р»СЊРЅРѕРіРѕ РєР°Р±РµР»СЏ");
            }
            else
            {
                try
                {
                    using var en = new MMDeviceEnumerator();
                    var srcDev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    var cableCapture = AudioDevices.FindPairedCapture(target.Name, AudioDevices.Capture());
                    if (cableCapture is null)
                    {
                        Log("РўРµСЃС‚ 4 SKIP: РЅРµ РЅР°Р№РґРµРЅР° РјРёРєСЂРѕС„РѕРЅРЅР°СЏ СЃС‚РѕСЂРѕРЅР° РєР°Р±РµР»СЏ");
                    }
                    else
                    {
                        using var engine = new AudioEngine();
                        engine.Start(new AudioEngine.StartOptions
                        {
                            SourceId = null,
                            TargetId = target.Id,
                            MicMix = true,
                            LatencyMs = 50
                        });

                        var mix = srcDev.AudioClient.MixFormat;
                        var sine = new SineSampleProvider(
                            WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels), 440, 0.4f);
                        using var sineOut = new WasapiOut(srcDev, AudioClientShareMode.Shared, true, 50);
                        sineOut.Init(mix.Encoding == WaveFormatEncoding.Pcm && mix.BitsPerSample == 16
                            ? (IWaveProvider)new SampleToWaveProvider16(sine)
                            : new SampleToWaveProvider(sine));
                        sineOut.Play();

                        int engPackets = 0;
                        float engPeak = 0f;
                        using var cap = new WasapiCapture(en.GetDevice(cableCapture.Id));
                        cap.DataAvailable += (_, e) =>
                        {
                            engPackets++;
                            if (cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                            {
                                var s = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
                                for (int i = 0; i < s.Length; i++)
                                {
                                    float a = Math.Abs(s[i]);
                                    if (a > engPeak) engPeak = a;
                                }
                            }
                            else if (cap.WaveFormat.Encoding == WaveFormatEncoding.Pcm &&
                                     cap.WaveFormat.BitsPerSample == 16)
                            {
                                var s = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
                                for (int i = 0; i < s.Length; i++)
                                {
                                    float a = Math.Abs(s[i] / 32768f);
                                    if (a > engPeak) engPeak = a;
                                }
                            }
                        };

                        Log("РўРµСЃС‚ 4: Р·Р°РїСѓСЃРєР°СЋ РґРІРёР¶РѕРє СЃ РјРёРєС€РµСЂРѕРј (РЅР° 2,5 СЃРµРєСѓРЅРґС‹ РїСЂРѕР·РІСѓС‡РёС‚ С‚РѕРЅ 440 Р“С† вЂ” СЌС‚Рѕ РЅРѕСЂРјР°)...");
                        cap.StartRecording();
                        Thread.Sleep(2500);
                        cap.StopRecording();
                        sineOut.Stop();
                        float micPeakSeen = engine.MicPeak;
                        string? engErr = engine.LastError;
                        engine.Stop();

                        Log($"РўРµСЃС‚ 4 (РґРІРёР¶РѕРє+РјРёРєС€РµСЂ, РјРёРєСЃ РјРёРєСЂРѕС„РѕРЅР°=РґР°): РїР°РєРµС‚РѕРІ={engPackets}, РїРёРє={engPeak:F4}, " +
                            $"РјРёРєСЂРѕС„РѕРЅРЅС‹Р№ РїРёРє={micPeakSeen:F4}, РѕС€РёР±РєР°={engErr ?? "РЅРµС‚"}");
                        if (engPeak > 0.02f && engPackets > 0 && engErr is null)
                            Log("  РўРµСЃС‚ 4 PASS: РјРёРєС€РµСЂ РЅРµ С‚РµСЂСЏРµС‚ РєР°РЅР°Р»С‹ вЂ” Р·РІСѓРє СЃС‚Р°Р±РёР»СЊРЅРѕ РґРѕС…РѕРґРёС‚ РґРѕ РєР°Р±РµР»СЏ");
                        else
                        {
                            Log("  РўРµСЃС‚ 4 FAIL: РїРѕСЃР»Рµ Р·Р°РїСѓСЃРєР° СЃ РјРёРєС€РµСЂРѕРј РЅР° СЃС‚РѕСЂРѕРЅРµ РєР°Р±РµР»СЏ С‚РёС€РёРЅР°/РѕС€РёР±РєР°");
                            pass = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"  РўРµСЃС‚ 4 РћРЁРР‘РљРђ: {ex}");
                    pass = false;
                }
            }

            // --- РўРµСЃС‚ 5: СЂРµР¶РёРј В«РїРѕ РїСЂРёР»РѕР¶РµРЅРёСЏРјВ» вЂ” Р·Р°С…РІР°С‚ СЃРІРѕРµРіРѕ РїСЂРѕС†РµСЃСЃР° + РјСѓС‚ РїСЂРёР»РѕР¶РµРЅРёСЏ ---
            Log(new string('-', 56));
            if (target is null)
            {
                Log("РўРµСЃС‚ 5 SKIP: РЅРµС‚ РІРёСЂС‚СѓР°Р»СЊРЅРѕРіРѕ РєР°Р±РµР»СЏ");
            }
            else
            {
                try
                {
                    using var en = new MMDeviceEnumerator();
                    var srcDev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    var cable = AudioDevices.FindPairedCapture(target.Name, AudioDevices.Capture());
                    if (cable is null)
                    {
                        Log("РўРµСЃС‚ 5 SKIP: РЅРµ РЅР°Р№РґРµРЅР° РјРёРєСЂРѕС„РѕРЅРЅР°СЏ СЃС‚РѕСЂРѕРЅР° РєР°Р±РµР»СЏ");
                    }
                    else
                    {
                        int myPid = Environment.ProcessId;
                        using var engine = new AudioEngine();
                        engine.Start(new AudioEngine.StartOptions
                        {
                            SourceId = null,
                            TargetId = target.Id,
                            CapturePids = new[] { myPid },
                            LatencyMs = 50
                        });

                        var mix = srcDev.AudioClient.MixFormat;
                        var sine = new SineSampleProvider(
                            WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels), 440, 0.4f);
                        using var sineOut = new WasapiOut(srcDev, AudioClientShareMode.Shared, true, 50);
                        sineOut.Init(mix.Encoding == WaveFormatEncoding.Pcm && mix.BitsPerSample == 16
                            ? (IWaveProvider)new SampleToWaveProvider16(sine)
                            : new SampleToWaveProvider(sine));
                        sineOut.Play();

                        float Measure(int ms)
                        {
                            // Р”СЂРµРЅР°Р¶: VB-Cable РѕС‚РґР°С‘С‚ РЅРµРїСЂРѕС‡РёС‚Р°РЅРЅСѓСЋ СЂР°РЅРµРµ РѕС‡РµСЂРµРґСЊ (РµС‘ РїРёС€РµС‚
                            // СЂРµРЅРґРµСЂ, РїРѕРєР° РЅРёРєС‚Рѕ РЅРµ С‡РёС‚Р°Р») вЂ” СЃРЅР°С‡Р°Р»Р° СЃР»РёРІР°РµРј, РїРѕС‚РѕРј РјРµСЂСЏРµРј Р¶РёРІРѕРµ.
                            using (var d = new WasapiCapture(en.GetDevice(cable.Id)))
                            {
                                d.StartRecording();
                                Thread.Sleep(500);
                                d.StopRecording();
                            }

                            float peak = 0f;
                            using var c = new WasapiCapture(en.GetDevice(cable.Id));
                            c.DataAvailable += (_, e) =>
                            {
                                if (c.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                                {
                                    var s = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
                                    for (int i = 0; i < s.Length; i++)
                                    {
                                        float a = Math.Abs(s[i]);
                                        if (a > peak) peak = a;
                                    }
                                }
                                else if (c.WaveFormat.Encoding == WaveFormatEncoding.Pcm &&
                                         c.WaveFormat.BitsPerSample == 16)
                                {
                                    var s = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
                                    for (int i = 0; i < s.Length; i++)
                                    {
                                        float a = Math.Abs(s[i] / 32768f);
                                        if (a > peak) peak = a;
                                    }
                                }
                            };
                            c.StartRecording();
                            Thread.Sleep(ms);
                            c.StopRecording();
                            return peak;
                        }

                        Log("РўРµСЃС‚ 5: СЂРµР¶РёРј В«РїРѕ РїСЂРёР»РѕР¶РµРЅРёСЏРјВ», СЃРІРѕР№ РїСЂРѕС†РµСЃСЃ РёРіСЂР°РµС‚ СЃРёРЅСѓСЃ (440 Р“С†)...");
                        float p1 = Measure(1500);
                        float out1 = engine.OutputPeak;
                        engine.SetAppMuted(myPid, true);
                        Thread.Sleep(800);
                        float out2 = engine.OutputPeak;
                        float p2 = Measure(1500);
                        engine.SetAppMuted(myPid, false);
                        Thread.Sleep(300);
                        float p3 = Measure(1500);
                        string pids = string.Join(",", engine.ActiveAppPids);
                        sineOut.Stop();
                        engine.Stop();

                        Log($"РўРµСЃС‚ 5 (РїРµСЂ-Р°РїРї РјСѓС‚): Р±С‹Р»Рѕ={p1:F4} (out={out1:F4}), " +
                            $"РІ РјСѓС‚Рµ={p2:F4} (out={out2:F4}), СЃРЅРѕРІР°={p3:F4}, pids=[{pids}]");
                        if (p1 > 0.02f && p2 < 0.01f && p3 > 0.02f)
                            Log("  РўРµСЃС‚ 5 PASS: РјСѓС‚ РїСЂРёР»РѕР¶РµРЅРёСЏ РіР»СѓС€РёС‚ С‚РѕР»СЊРєРѕ РµРіРѕ Р·РІСѓРє, СЃРЅСЏС‚РёРµ РјСѓС‚Р° РІРѕР·РІСЂР°С‰Р°РµС‚ РїРѕС‚РѕРє");
                        else
                        {
                            Log("  РўРµСЃС‚ 5 FAIL: РїРµСЂ-Р°РїРї РјСѓС‚ РЅРµ СЃСЂР°Р±РѕС‚Р°Р» РєР°Рє РѕР¶РёРґР°Р»РѕСЃСЊ");
                            pass = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"  РўРµСЃС‚ 5 РћРЁРР‘РљРђ: {ex}");
                    pass = false;
                }
            }
        }
        catch (Exception ex)
        {
            Log("РљСЂРёС‚РёС‡РµСЃРєР°СЏ РѕС€РёР±РєР°: " + ex);
            pass = false;
        }

        Log(new string('-', 56));
        Log(pass ? "РРўРћР“: PASS" : "РРўРћР“: FAIL");

        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceBridge");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "selftest.log"), log, System.Text.Encoding.UTF8);
        }
        catch
        {
            /* log file is best-effort */
        }

        return pass ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(int access, bool inherit, int id);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetThreadDescription(IntPtr h, out IntPtr desc);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr h);

    private static Dictionary<int, long> SampleThreads()
    {
        var d = new Dictionary<int, long>();
        foreach (System.Diagnostics.ProcessThread t in System.Diagnostics.Process.GetCurrentProcess().Threads)
        {
            IntPtr h = OpenThread(0x1040, false, t.Id);
            if (h == IntPtr.Zero) continue;
            if (GetThreadTimes(h, out _, out _, out long k, out long u))
                d[t.Id] = k + u;
            CloseHandle(h);
        }
        return d;
    }

    private static string ThreadName(int id)
    {
        IntPtr h = OpenThread(0x1040, false, id);
        if (h == IntPtr.Zero) return "?";
        try
        {
            if (GetThreadDescription(h, out IntPtr desc) == 0 && desc != IntPtr.Zero)
            {
                string? s = System.Runtime.InteropServices.Marshal.PtrToStringUni(desc);
                LocalFree(desc);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        catch { /* ignore */ }
        finally { CloseHandle(h); }
        return "?";
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(
        IntPtr hThread, int cls, out IntPtr info, int len, out int retLen);

    private static string ThreadModule(int id)
    {
        IntPtr h = OpenThread(0x1040, false, id);
        if (h == IntPtr.Zero) return "?";
        try
        {
            if (NtQueryInformationThread(h, 9, out IntPtr start, IntPtr.Size, out _) != 0 || start == IntPtr.Zero)
                return "?";
            string mod = "?";
            ulong saddr = (ulong)start.ToInt64();
            ulong rva = 0;
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                if (saddr >= (ulong)m.BaseAddress && saddr < (ulong)m.BaseAddress + (ulong)m.ModuleMemorySize)
                {
                    mod = m.ModuleName;
                    rva = saddr - (ulong)m.BaseAddress;
                    break;
                }
            if (mod == "?") return $"0x{saddr:X}";
            return $"{mod}!{NearestExport(mod, rva)}";
        }
        catch { return "?"; }
        finally { CloseHandle(h); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SuspendThread(IntPtr h);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr h);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadContext(IntPtr h, byte[] ctx);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static string StartInfo(int id, out ulong rvaOut)
    {
        rvaOut = 0;
        IntPtr h = OpenThread(0x1040, false, id);
        if (h == IntPtr.Zero) return "?";
        try
        {
            if (NtQueryInformationThread(h, 9, out IntPtr start, IntPtr.Size, out _) != 0 || start == IntPtr.Zero)
                return "?";
            ulong saddr = (ulong)start.ToInt64();
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                if (saddr >= (ulong)m.BaseAddress && saddr < (ulong)m.BaseAddress + (ulong)m.ModuleMemorySize)
                {
                    ulong rva = saddr - (ulong)m.BaseAddress;
                    rvaOut = rva;
                    string? sym = SymName(m.ModuleName, 0, rva);
                    return sym is null ? $"{m.ModuleName}+0x{rva:X}" : $"{m.ModuleName}!{sym}";
                }
            return $"0x{saddr:X}";
        }
        finally { CloseHandle(h); }
    }

    private static string ModuleOf(ulong addr, out ulong rva)
    {
        rva = 0;
        foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
            if (addr >= (ulong)m.BaseAddress && addr < (ulong)m.BaseAddress + (ulong)m.ModuleMemorySize)
            {
                rva = addr - (ulong)m.BaseAddress;
                return m.ModuleName;
            }
        return "JIT/unknown";
    }

    private static readonly Dictionary<string, string> _exportPaths = new();
    private static readonly Dictionary<string, byte[]> _exportImgs = new();

    private static string NearestExport(string moduleName, ulong rva)
    {
        try
        {
            if (!_exportImgs.TryGetValue(moduleName, out byte[]? img))
            {
                if (!_exportPaths.TryGetValue(moduleName, out string? path))
                {
                    path = null;
                    foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                        if (m.ModuleName == moduleName) { path = m.FileName; break; }
                    _exportPaths[moduleName] = path ?? "";
                }
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "?";
                img = System.IO.File.ReadAllBytes(path);
                _exportImgs[moduleName] = img;
            }
            int pe = BitConverter.ToInt32(img, 0x3C);
            int opt = pe + 24;
            ushort magic = BitConverter.ToUInt16(img, opt);
            int dirRva = magic == 0x20B ? opt + 112 : opt + 96;
            uint expRva = BitConverter.ToUInt32(img, dirRva);
            if (expRva == 0) return "?";
            ushort nSec = BitConverter.ToUInt16(img, pe + 6);
            ushort sizeOpt = BitConverter.ToUInt16(img, pe + 20);
            int secTbl = opt + sizeOpt;
            long ToFile(uint rva2)
            {
                for (int s = 0; s < nSec; s++)
                {
                    int so = secTbl + s * 40;
                    uint va = BitConverter.ToUInt32(img, so + 12);
                    uint vsz = BitConverter.ToUInt32(img, so + 8);
                    uint raw = BitConverter.ToUInt32(img, so + 20);
                    uint rsz = BitConverter.ToUInt32(img, so + 16);
                    uint sz = Math.Max(vsz, rsz);
                    if (rva2 >= va && rva2 < va + sz) return raw + (rva2 - va);
                }
                return rva2;
            }
            long expOff = ToFile(expRva);
            uint funcs = BitConverter.ToUInt32(img, (int)expOff + 28);
            uint names = BitConverter.ToUInt32(img, (int)expOff + 32);
            uint nameOrds = BitConverter.ToUInt32(img, (int)expOff + 36);
            int count = BitConverter.ToInt32(img, (int)expOff + 24);
            long funcOff = ToFile(funcs);
            long namesOff = ToFile(names);
            long ordsOff = ToFile(nameOrds);
            string? best = null;
            ulong bestRva = 0;
            for (int i = 0; i < count; i++)
            {
                uint nRva = BitConverter.ToUInt32(img, (int)namesOff + i * 4);
                ushort fi = BitConverter.ToUInt16(img, (int)ordsOff + i * 2);
                uint f = BitConverter.ToUInt32(img, (int)funcOff + fi * 4);
                if (f == 0 || f > rva) continue;
                if (f > bestRva)
                {
                    bestRva = f;
                    int strOff = (int)ToFile(nRva);
                    int end = Array.IndexOf(img, (byte)0, strOff);
                    best = System.Text.Encoding.ASCII.GetString(img, strOff, end - strOff);
                }
            }
            return best is null ? "?" : $"{best}+0x{rva - bestRva:X}";
        }
        catch { return "?"; }
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr historyTable);

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern IntPtr RtlVirtualUnwind(uint handlerType, ulong controlPc, ulong imageBase,
        IntPtr functionEntry, IntPtr contextRecord, out IntPtr handlerData, out ulong establisherFrame,
        IntPtr contextPointers);

    [System.Runtime.InteropServices.DllImport("dbghelp.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SymInitialize(IntPtr hProcess, string userSearchPath, bool invadeProcess);

    [System.Runtime.InteropServices.DllImport("dbghelp.dll")]
    private static extern uint SymSetOptions(uint symOptions);

    [System.Runtime.InteropServices.DllImport("dbghelp.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern ulong SymLoadModuleEx(IntPtr hProcess, IntPtr hFile, string imageFileName,
        string moduleName, ulong baseOfDll, uint sizeOfDll, IntPtr data, uint flags);

    [System.Runtime.InteropServices.DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymFromAddr(IntPtr hProcess, ulong address, out ulong displacement, byte[] symbolInfo);

    private static bool _symsReady;
    private static IntPtr _symProc = IntPtr.Zero;

    private static string SymName(string moduleName, ulong addrInModuleBase, ulong rva)
    {
        try
        {
            if (!_symsReady)
            {
                SymSetOptions(0x4 | 0x10 | 0x2);
                string cache = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vbsym");
                System.IO.Directory.CreateDirectory(cache);
                _symProc = OpenProcess(0x0010 | 0x0002 | 0x0400, false,
                    (uint)System.Diagnostics.Process.GetCurrentProcess().Id);
                if (_symProc == IntPtr.Zero)
                    _symProc = System.Diagnostics.Process.GetCurrentProcess().Handle;
                _symsReady = SymInitialize(_symProc, cache, false);
                Console.WriteLine($"syminit={_symsReady} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()} cache={cache}");
            }
            if (!_symsReady) return null;
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                if (m.ModuleName == moduleName)
                {
                    ulong baseAddr = (ulong)m.BaseAddress;
                    ulong loaded = SymLoadModuleEx(_symProc, IntPtr.Zero,
                        m.FileName, moduleName, baseAddr, (uint)m.ModuleMemorySize, IntPtr.Zero, 0);
                    long err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    Console.WriteLine($"symload {moduleName} base=0x{baseAddr:X} ret=0x{loaded:X} err={err}");
                    if (loaded == 0 && err != 0 && err != 487) return null;
                    byte[] buf = new byte[80 + 2048];
                    BitConverter.GetBytes(88).CopyTo(buf, 0);
                    BitConverter.GetBytes(2000).CopyTo(buf, 76);
                    if (!SymFromAddr(_symProc, baseAddr + rva,
                        out ulong disp, buf))
                    {
                        long e2 = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        Console.WriteLine($"symfromaddr fail addr=0x{baseAddr + rva:X} err={e2}");
                        return null;
                    }
                    int nameLen = BitConverter.ToInt32(buf, 72);
                    string name = System.Text.Encoding.ASCII.GetString(buf, 80, nameLen);
                    return $"{name}+0x{disp:X}";
                }
        }
        catch { /* ignore */ }
        return null;
    }

    private static string[] Unwind(byte[] ctx)
    {
        var frames = new List<string>();
        IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(0x1000 + 16);
        try
        {
            long aligned = (buf.ToInt64() + 15) & ~15L;
            System.Runtime.InteropServices.Marshal.Copy(ctx, 0, (IntPtr)aligned, ctx.Length);
            byte[] c = new byte[0x4D0];
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)aligned, c, 0, c.Length);
            for (int depth = 0; depth < 40; depth++)
            {
                ulong rip = BitConverter.ToUInt64(c, 0xF8);
                if (rip < 0x10000) break;
                string mod = ModuleOf(rip, out ulong rva);
                string sym = mod == "JIT/unknown" ? $"jit+0x{rva:X}" : SymName(mod, 0, rva) ?? $"{mod}!{NearestExport(mod, rva)}";
                frames.Add(sym);
                break;
            }
        }
        catch { /* ignore */ }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        return frames.ToArray();
    }

    private static void PrintPcHistogram(int id, int samples, int delayMs)
    {
        IntPtr h = OpenThread(0xFFFF, false, id);
        if (h == IntPtr.Zero) return;
        var counts = new Dictionary<string, int>();
        try
        {
            for (int i = 0; i < samples; i++)
            {
                byte[] ctx = new byte[0x1000];
                System.Buffer.BlockCopy(BitConverter.GetBytes(0x100001u), 0, ctx, 0x30, 4);
                if (SuspendThread(h) == 0xFFFFFFFF) break;
                bool ok = GetThreadContext(h, ctx);
                ResumeThread(h);
                if (!ok) continue;
                ulong rip = BitConverter.ToUInt64(ctx, 0xF8);
                string mod = ModuleOf(rip, out ulong rva);
                string key = mod == "JIT/unknown" ? mod : $"{mod}!{NearestExport(mod, rva)}";
                counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                if (i == 0)
                    Console.WriteLine($"    unwind id={id}: {string.Join(" <- ", Unwind(ctx))}");
                System.Threading.Thread.Sleep(delayMs);
            }
        }
        catch { /* ignore */ }
        finally { CloseHandle(h); }
        foreach (var kv in counts.OrderByDescending(x => x.Value))
            Console.WriteLine($"    pc id={id}: {kv.Key} x{kv.Value}/{samples}");
    }

    private static bool samplePc;

    private static void PrintThreadDeltas(Dictionary<int, long> t0, int sec)
    {
        var t1 = SampleThreads();
        var rows = new List<(string name, int id, double pct, string start, string mod)>();
        foreach (var kv in t0)
        {
            if (!t1.TryGetValue(kv.Key, out long v1)) continue;
            double ms = (v1 - kv.Value) / 10000.0;
            double pct = ms / (sec * 1000.0) * 100.0;
            if (pct < 0.5) continue;
            string st = "?";
            try
            {
                foreach (System.Diagnostics.ProcessThread pt in System.Diagnostics.Process.GetCurrentProcess().Threads)
                    if (pt.Id == kv.Key) { st = pt.StartTime.ToString("HH:mm:ss.fff"); break; }
            }
            catch { /* ignore */ }
            rows.Add((ThreadName(kv.Key), kv.Key, pct, st, ThreadModule(kv.Key)));
        }
        foreach (var r in rows.OrderByDescending(x => x.pct))
        {
            Console.WriteLine($"    thread '{r.name}' id={r.id} start={r.start} cpu={r.pct:F1}% mod={r.mod}");
            if (samplePc && r.pct >= 5.0)
                PrintPcHistogram(r.id, 40, 2);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GlobalGetAtomName(System.UInt16 hAtom, System.Text.StringBuilder lpBuffer, int nSize);

    private static string MsgName(int msg)
    {
        if (msg < 0xC000) return msg.ToString();
        try
        {
            var sb = new System.Text.StringBuilder(256);
            int n = GlobalGetAtomName((System.UInt16)(msg - 0xC000), sb, sb.Capacity);
            return n > 0 ? $"{msg} '{sb}'" : msg.ToString();
        }
        catch { return msg.ToString(); }
    }

    private static void PrintMessages(Dictionary<int, int> msgCounts)
    {
        foreach (var m in msgCounts.OrderByDescending(x => x.Value).Take(12))
            Console.WriteLine($"    msg 0x{m.Key:X} ({MsgName(m.Key)}) x{m.Value}");
    }

    /// <summary>РљРѕРЅС‚СЂРѕР»СЊРЅС‹Р№ Р·Р°РјРµСЂ: РіРѕР»РѕРµ WPF-РѕРєРЅРѕ Р±РµР· РєРѕРґР° РїСЂРёР»РѕР¶РµРЅРёСЏ.</summary>
    public static void StartBareBench(System.Windows.Window win, int sec)
    {
        if (sec < 3) sec = 3;
        samplePc = false;
        double cpu0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var th0 = SampleThreads();
        var msgCounts = new Dictionary<int, int>();
        try
        {
            var hsrc = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(win).Handle);
            hsrc?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                msgCounts[msg] = msgCounts.TryGetValue(msg, out int c) ? c + 1 : 1;
                return IntPtr.Zero;
            });
        }
        catch { /* ignore */ }
        int bareFrames = 0;
        void BareOnFrame(object? s, EventArgs e) => bareFrames++;
        System.Windows.Media.CompositionTarget.Rendering += BareOnFrame;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int ticks = 0;
        timer.Tick += (_, _) =>
        {
            if (++ticks < sec) return;
            timer.Stop();
            double cpu1 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            long allocMb = (GC.GetTotalAllocatedBytes(precise: true) - alloc0) / (1024 * 1024);
            double cpuPct = (cpu1 - cpu0) / (sec * 1000.0) * 100.0;
            System.Windows.Media.CompositionTarget.Rendering -= BareOnFrame;
            Console.WriteLine($"bench mode=bare sec={sec} cpu={cpuPct:F1}% alloc={allocMb}MB frames={bareFrames}");
            PrintThreadDeltas(th0, sec);
            PrintMessages(msgCounts);
            System.Windows.Application.Current.Shutdown(0);
        };
        timer.Start();
    }

    private static void StripEffects(System.Windows.DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.UIElement u && u.Effect != null)
                u.Effect = null;
            StripEffects(child);
        }
    }

    /// <summary>Р’СЂРµРјРµРЅРЅС‹Р№ GUI-Р·Р°РјРµСЂ: --bench gui|guisys|guiapp [СЃРµРєСѓРЅРґ] вЂ” РѕРєРЅРѕ + РґРІРёР¶РѕРє + СЃРёРЅСѓСЃ.</summary>
    public static void StartGuiBench(string mode, int sec, VoiceBridge.MainWindow win)
    {
        if (sec < 3) sec = 3;
        samplePc = mode.Contains("-sample", StringComparison.Ordinal);
        var st = Services.AppSettings.Load();
        double cpu0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        long ws0 = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var th0 = SampleThreads();
        string? error = null;
        WasapiOut? sineOut = null;
        if (mode.Contains("-nt", StringComparison.Ordinal))
            win.BenchStopTimers();
        if (mode.Contains("-norouting", StringComparison.Ordinal))
            win.ColRouting.Visibility = System.Windows.Visibility.Collapsed;
        if (mode.Contains("-nosettings", StringComparison.Ordinal))
            win.ColSettings.Visibility = System.Windows.Visibility.Collapsed;
        if (mode.Contains("-noheader", StringComparison.Ordinal))
            win.HeaderRow.Visibility = System.Windows.Visibility.Collapsed;
        if (mode.Contains("-nograd", StringComparison.Ordinal))
            win.GradientHost.Visibility = System.Windows.Visibility.Collapsed;
        if (mode.Contains("-allhide", StringComparison.Ordinal))
        {
            win.HeaderRow.Visibility = System.Windows.Visibility.Collapsed;
            win.MainScroll.Visibility = System.Windows.Visibility.Collapsed;
            win.GradientHost.Visibility = System.Windows.Visibility.Collapsed;
            win.ColRouting.Visibility = System.Windows.Visibility.Collapsed;
            win.ColSettings.Visibility = System.Windows.Visibility.Collapsed;
        }
        if (mode.Contains("-noscroll", StringComparison.Ordinal))
            win.MainScroll.Visibility = System.Windows.Visibility.Collapsed;
        if (mode.Contains("-small", StringComparison.Ordinal))
        {
            win.Width = 480;
            win.Height = 320;
        }
        if (mode.Contains("-noeffect", StringComparison.Ordinal))
            StripEffects(win);
        if (mode.Contains("-cnull", StringComparison.Ordinal))
            win.Content = null;
        if (mode.Contains("-strip", StringComparison.Ordinal))
            win.Content = new System.Windows.Controls.Border
            {
                Background = System.Windows.Media.Brushes.Black
            };
        if (mode.Contains("-bg", StringComparison.Ordinal))
            win.Background = System.Windows.Media.Brushes.Black;
        if (mode.Contains("-hide", StringComparison.Ordinal))
            win.Hide();
        else if (mode.Contains("-min", StringComparison.Ordinal))
            win.WindowState = System.Windows.WindowState.Minimized;
        Console.WriteLine($"info tier={(System.Windows.Media.RenderCapability.Tier >> 16)} {win.TimersState()}" +
                          $" hdr={win.HeaderRow.Visibility} scr={win.MainScroll.Visibility} grad={win.GradientHost.Visibility}" +
                          $" rc={win.ColRouting.Visibility} st={win.ColSettings.Visibility}" +
                          $" content={(win.Content is null ? "null" : win.Content.GetType().Name)}" +
                          $" cores={Environment.ProcessorCount}");

        var msgCounts = new Dictionary<int, int>();
        int msgSeen = 0;
        bool blk = mode.Contains("-blk", StringComparison.Ordinal);
        try
        {
            var hsrc = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(win).Handle);
            hsrc?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                msgCounts[msg] = msgCounts.TryGetValue(msg, out int c) ? c + 1 : 1;
                if (blk && (msg == 0xC313 || msg == 0x14))
                    handled = true;
                return IntPtr.Zero;
            });
        }
        catch { /* ignore */ }

        bool withEngine = mode.StartsWith("guisys", StringComparison.Ordinal) ||
                          mode.StartsWith("guiapp", StringComparison.Ordinal);
        if (withEngine)
        {
            try
            {
                win.Engine.Start(new AudioEngine.StartOptions
                {
                    SourceId = st.SourceId,
                    TargetId = st.TargetId,
                    MicId = st.MicId,
                    MicMix = st.MicMix,
                    CapturePids = mode.StartsWith("guiapp", StringComparison.Ordinal)
                        ? new[] { Environment.ProcessId }
                        : null,
                    LatencyMs = st.LatencyMs
                });

                using var en = new MMDeviceEnumerator();
                MMDevice srcDev;
                if (string.IsNullOrEmpty(st.SourceId))
                    srcDev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                else
                    srcDev = en.GetDevice(st.SourceId);
                var mix = srcDev.AudioClient.MixFormat;
                var sine = new SineSampleProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels), 440, 0.3f);
                var outp = new WasapiOut(srcDev, AudioClientShareMode.Shared, true, 50);
                outp.Init(mix.Encoding == WaveFormatEncoding.Pcm && mix.BitsPerSample == 16
                    ? (IWaveProvider)new SampleToWaveProvider16(sine)
                    : new SampleToWaveProvider(sine));
                outp.Play();
                sineOut = outp;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        int frames = 0;
        void OnFrame(object? s, EventArgs e) => frames++;
        if (mode.Contains("-fr", StringComparison.Ordinal))
            System.Windows.Media.CompositionTarget.Rendering += OnFrame;

        int refTid = 0;
        var refThread = new System.Threading.Thread(() =>
        {
            refTid = (int)GetCurrentThreadId();
            System.Threading.Thread.Sleep(600000);
        }) { IsBackground = true };
        refThread.Start();

        int suspendTid = 0;
        IntPtr susHandle = IntPtr.Zero;
        int ticks = 0;
        bool dumpedAll = false;
        double lastCpu = cpu0;
        timer.Tick += (_, _) =>
        {
            double nowCpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            int msgNow = 0;
            foreach (var v in msgCounts.Values) msgNow += v;
            Console.WriteLine($"sec {ticks + 1} cpu={((nowCpu - lastCpu) / 1000.0) * 100:F1}% msgs={msgNow - msgSeen}");
            msgSeen = msgNow;
            lastCpu = nowCpu;
            if ((mode.Contains("-sample", StringComparison.Ordinal) || mode.Contains("-sus", StringComparison.Ordinal))
                && ticks == sec / 2)
            {
                var mid = SampleThreads();
                if (!dumpedAll)
                {
                    dumpedAll = true;
                    foreach (var kv in mid.OrderByDescending(x => x.Value).Take(20))
                    {
                        long cpuMs = th0.ContainsKey(kv.Key) ? kv.Value - th0[kv.Key] : -1;
                        Console.WriteLine($"  thr id={kv.Key} name='{ThreadName(kv.Key)}' start={StartInfo(kv.Key, out _)} dcpu={cpuMs / 10000.0:F1}%");
                    }
                }
                var hot = mid.Where(kv => th0.ContainsKey(kv.Key))
                    .Select(kv => (kv.Key, pct: (kv.Value - th0[kv.Key]) / 10000.0 / ((ticks + 1) * 1000.0) * 100.0))
                    .OrderByDescending(x => x.pct).Take(2).ToList();
                foreach (var h in hot)
                    if (h.pct >= 3.0)
                    {
                        Console.WriteLine($"midrun hot id={h.Key} cpu={h.pct:F1}% name='{ThreadName(h.Key)}' start={StartInfo(h.Key, out _)}");
                        if (refTid != 0)
                            Console.WriteLine($"ref managed thread id={refTid} start={StartInfo(refTid, out _)}");
                        if (mode.Contains("-sus", StringComparison.Ordinal))
                        {
                            susHandle = OpenThread(0xFFFF, false, h.Key);
                            if (susHandle != IntPtr.Zero && SuspendThread(susHandle) != 0xFFFFFFFF)
                            {
                                suspendTid = h.Key;
                                Console.WriteLine($"suspended id={h.Key}");
                            }
                        }
                        else
                            PrintPcHistogram(h.Key, 60, 2);
                    }
            }
            if (mode.Contains("-sus", StringComparison.Ordinal) && suspendTid != 0 && ticks == sec / 2 + 3)
            {
                ResumeThread(susHandle);
                CloseHandle(susHandle);
                susHandle = IntPtr.Zero;
                Console.WriteLine($"resumed id={suspendTid}");
                suspendTid = 0;
            }
            if (++ticks < sec) return;
            timer.Stop();
            double cpu1 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            long ws1 = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            double cpuPct = (cpu1 - cpu0) / (sec * 1000.0) * 100.0;
            long allocMb = (GC.GetTotalAllocatedBytes(precise: true) - alloc0) / (1024 * 1024);
            Console.WriteLine($"bench mode={mode} sec={sec} cpu={cpuPct:F1}% ws={ws0 / 1048576}MB->{ws1 / 1048576}MB" +
                              $" alloc={allocMb}MB gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}" +
                              $" frames={frames}" +
                              (error is null ? "" : " ERROR=" + error));
            System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
            PrintThreadDeltas(th0, sec);
            PrintMessages(msgCounts);
            try { sineOut?.Stop(); sineOut?.Dispose(); } catch { /* ignore */ }
            try { win.Engine.Stop(); } catch { /* ignore */ }
            System.Windows.Application.Current.Shutdown(0);
        };
        timer.Start();
    }

    /// <summary>Р’СЂРµРјРµРЅРЅС‹Р№ Р·Р°РјРµСЂ CPU/РїР°РјСЏС‚Рё: --bench idle|sys|app [СЃРµРєСѓРЅРґ].</summary>
    public static int RunBench(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--bench", StringComparison.OrdinalIgnoreCase));
        string mode = i >= 0 && i + 1 < args.Length ? args[i + 1] : "idle";
        int sec = 8;
        if (i >= 0 && i + 2 < args.Length && int.TryParse(args[i + 2], out int s2)) sec = s2;
        if (sec < 2) sec = 2;
        samplePc = false;

        var st = Services.AppSettings.Load();
        bool micMix = !mode.EndsWith("-no", StringComparison.Ordinal);
        int latency = mode.Contains("-l100", StringComparison.Ordinal) ? 100 : st.LatencyMs;
        double cpu0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        long ws0 = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        var th0 = SampleThreads();

        AudioEngine? engine = null;
        string? error = null;
        try
        {
            if (mode != "idle")
            {
                engine = new AudioEngine();
                engine.Start(new AudioEngine.StartOptions
                {
                    SourceId = st.SourceId,
                    TargetId = st.TargetId,
                    MicId = st.MicId,
                    MicMix = micMix,
                    CapturePids = mode.StartsWith("app", StringComparison.Ordinal)
                        ? new[] { Environment.ProcessId }
                        : null,
                    LatencyMs = latency
                });
            }
            Thread.Sleep(sec * 1000);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            try { engine?.Stop(); } catch { /* ignore */ }
            try { engine?.Dispose(); } catch { /* ignore */ }
        }

        double cpu1 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        long ws1 = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        double cpuPct = (cpu1 - cpu0) / (sec * 1000.0) * 100.0;
        Console.WriteLine($"bench mode={mode} sec={sec} cpu={cpuPct:F1}% ws={ws0 / 1048576}MB->{ws1 / 1048576}MB" +
                          (error is null ? "" : " ERROR=" + error));
        PrintThreadDeltas(th0, sec);
        return error is null ? 0 : 1;
    }

}

