namespace ReplayPad.Core;

/// <summary>
/// Rolling history of peak levels, one entry per 100 ms, mirroring the
/// audio ring buffer's timeline. Cheap enough to snapshot several times a
/// second for the waveform display.
/// </summary>
public sealed class WaveformHistory
{
    public const int EntriesPerSecond = 10;

    private readonly object _lock = new();
    private readonly float[] _peaks;
    private int _pos;
    private bool _full;

    public WaveformHistory(int bufferSeconds)
    {
        _peaks = new float[Math.Max(1, bufferSeconds * EntriesPerSecond)];
    }

    public void Add(float peak)
    {
        lock (_lock)
        {
            _peaks[_pos] = peak;
            _pos = (_pos + 1) % _peaks.Length;
            if (_pos == 0)
                _full = true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _pos = 0;
            _full = false;
        }
    }

    /// <summary>Chronological copy, oldest first.</summary>
    public float[] Snapshot()
    {
        lock (_lock)
        {
            if (!_full)
                return _peaks[.._pos];
            var result = new float[_peaks.Length];
            int tail = _peaks.Length - _pos;
            Array.Copy(_peaks, _pos, result, 0, tail);
            Array.Copy(_peaks, 0, result, tail, _pos);
            return result;
        }
    }
}
