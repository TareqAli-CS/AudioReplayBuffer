namespace AudioReplayBuffer.Core;

/// <summary>
/// Fixed-size ring buffer of raw PCM bytes. Writers overwrite the oldest
/// audio once full; Snapshot returns the content in chronological order.
/// </summary>
public sealed class CircularAudioBuffer
{
    private readonly byte[] _buffer;
    private readonly object _lock = new();
    private readonly int _bytesPerSecond;
    private int _writePos;
    private bool _full;

    public CircularAudioBuffer(int capacityBytes, int blockAlign, int bytesPerSecond)
    {
        capacityBytes -= capacityBytes % blockAlign;
        _buffer = new byte[capacityBytes];
        _bytesPerSecond = bytesPerSecond;
    }

    public void Write(byte[] data, int count)
    {
        lock (_lock)
        {
            if (count >= _buffer.Length)
            {
                Buffer.BlockCopy(data, count - _buffer.Length, _buffer, 0, _buffer.Length);
                _writePos = 0;
                _full = true;
                return;
            }

            int firstPart = Math.Min(count, _buffer.Length - _writePos);
            Buffer.BlockCopy(data, 0, _buffer, _writePos, firstPart);
            int remaining = count - firstPart;

            if (remaining > 0)
            {
                Buffer.BlockCopy(data, firstPart, _buffer, 0, remaining);
                _writePos = remaining;
                _full = true;
            }
            else
            {
                _writePos += firstPart;
                if (_writePos == _buffer.Length)
                {
                    _writePos = 0;
                    _full = true;
                }
            }
        }
    }

    /// <summary>Discards all buffered audio. Saved files are unaffected.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _writePos = 0;
            _full = false;
        }
    }

    /// <summary>Oldest-to-newest copy of everything currently buffered.</summary>
    public byte[] Snapshot()
    {
        lock (_lock)
        {
            if (!_full)
            {
                var partial = new byte[_writePos];
                Buffer.BlockCopy(_buffer, 0, partial, 0, _writePos);
                return partial;
            }

            var result = new byte[_buffer.Length];
            int tail = _buffer.Length - _writePos;
            Buffer.BlockCopy(_buffer, _writePos, result, 0, tail);
            Buffer.BlockCopy(_buffer, 0, result, tail, _writePos);
            return result;
        }
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_lock)
            {
                int bytes = _full ? _buffer.Length : _writePos;
                return TimeSpan.FromSeconds((double)bytes / _bytesPerSecond);
            }
        }
    }
}
