using NAudio.Wave;

namespace ReplayPad.Core;

/// <summary>
/// Reduces a multi-channel (>2) source to stereo by taking the first two
/// channels (front left/right). Surround mix formats are rare for capture
/// devices, but a 5.1/7.1 default output device would hit this path.
/// </summary>
public sealed class ChannelDownmixProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = [];

    public WaveFormat WaveFormat { get; }

    public ChannelDownmixProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        int needed = frames * _sourceChannels;
        if (_sourceBuffer.Length < needed)
            _sourceBuffer = new float[needed];

        int sourceRead = _source.Read(_sourceBuffer, 0, needed);
        int framesRead = sourceRead / _sourceChannels;

        for (int f = 0; f < framesRead; f++)
        {
            buffer[offset + f * 2] = _sourceBuffer[f * _sourceChannels];
            buffer[offset + f * 2 + 1] = _sourceBuffer[f * _sourceChannels + 1];
        }

        return framesRead * 2;
    }
}
