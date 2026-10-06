using NAudio.Wave;

namespace VoiceBridge.Audio;

/// <summary>Приводит число каналов источника к нужному (mono↔stereo и базовый downmix/upmix).</summary>
public sealed class ChannelAdapterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _outChannels;
    private readonly int _inChannels;

    public ChannelAdapterSampleProvider(ISampleProvider source, int outChannels)
    {
        _source = source;
        _outChannels = outChannels;
        _inChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _outChannels;
        if (frames <= 0) return 0;

        int inCount = frames * _inChannels;
        if (_inBuf.Length < inCount) _inBuf = new float[inCount];
        int read = _source.Read(_inBuf, 0, inCount);
        int readFrames = read / _inChannels;
        if (readFrames <= 0) return 0;

        if (_inChannels == _outChannels)
        {
            Array.Copy(_inBuf, 0, buffer, offset, readFrames * _outChannels);
        }
        else if (_inChannels == 1)
        {
            for (int f = 0; f < readFrames; f++)
            {
                float v = _inBuf[f];
                for (int c = 0; c < _outChannels; c++)
                    buffer[offset + f * _outChannels + c] = v;
            }
        }
        else if (_outChannels == 1)
        {
            float norm = 1f / _inChannels;
            for (int f = 0; f < readFrames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < _inChannels; c++) sum += _inBuf[f * _inChannels + c];
                buffer[offset + f] = sum * norm;
            }
        }
        else
        {
            for (int f = 0; f < readFrames; f++)
                for (int c = 0; c < _outChannels; c++)
                    buffer[offset + f * _outChannels + c] =
                        c < _inChannels ? _inBuf[f * _inChannels + c] : 0f;
        }

        return readFrames * _outChannels;
    }

    private float[] _inBuf = Array.Empty<float>();
}

/// <summary>Пропускает поток и измеряет пик после всех обработок (то, что реально уйдёт в микрофон).</summary>
public sealed class PeakTapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Action<float> _onPeak;

    public PeakTapSampleProvider(ISampleProvider source, Action<float> onPeak)
    {
        _source = source;
        _onPeak = onPeak;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float peak = 0f;
        for (int i = 0; i < read; i++)
        {
            float a = buffer[offset + i];
            if (a < 0f) a = -a;
            if (a > peak) peak = a;
        }
        if (read > 0) _onPeak(peak);
        return read;
    }
}

/// <summary>Мягкое ограничение после микшера, чтобы перегруз не «рвал» поток.</summary>
public sealed class ClampSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public ClampSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        for (int i = 0; i < read; i++)
        {
            float v = buffer[offset + i];
            if (v > 1f) buffer[offset + i] = 1f;
            else if (v < -1f) buffer[offset + i] = -1f;
        }
        return read;
    }
}

/// <summary>Тестовый тон для самопроверки пути вывода.</summary>
public sealed class SineSampleProvider : ISampleProvider
{
    private readonly double _freq;
    private readonly float _amp;
    private readonly int _channels;
    private double _phase;

    public SineSampleProvider(WaveFormat format, double freq = 440, float amp = 0.5f)
    {
        WaveFormat = format;
        _freq = freq;
        _amp = amp;
        _channels = format.Channels;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _channels;
        double step = 2 * Math.PI * _freq / WaveFormat.SampleRate;
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(Math.Sin(_phase) * _amp);
            _phase += step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            for (int c = 0; c < _channels; c++)
                buffer[offset + i * _channels + c] = v;
        }
        return frames * _channels;
    }
}
