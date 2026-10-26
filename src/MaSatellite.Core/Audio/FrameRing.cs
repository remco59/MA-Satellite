namespace MaSatellite.Core.Audio;

/// <summary>Growable circular buffer of interleaved float frames addressed by absolute frame index.</summary>
internal sealed class FrameRing
{
    private readonly int _channels;
    private readonly long _maxFrames;
    private float[] _data;
    private long _capacity; // power of two, in frames

    public FrameRing(int channels, int initialFrames, long maxFrames)
    {
        _channels = channels;
        _maxFrames = maxFrames;
        _capacity = 1;
        while (_capacity < initialFrames)
        {
            _capacity <<= 1;
        }

        _data = new float[_capacity * channels];
    }

    /// <summary>Absolute index of the first stored frame.</summary>
    public long Start { get; private set; }

    /// <summary>Absolute index one past the last stored frame.</summary>
    public long End { get; private set; }

    public long Count => End - Start;

    /// <summary>Appends frames; returns the number actually stored (less than offered only at the hard size cap).</summary>
    public int Append(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / _channels;
        var room = _maxFrames - Count;
        if (frames > room)
        {
            frames = (int)Math.Max(room, 0);
        }

        EnsureCapacity(Count + frames);
        var mask = _capacity - 1;
        for (var i = 0; i < frames; i++)
        {
            var dst = (int)((End + i) & mask) * _channels;
            samples.Slice(i * _channels, _channels).CopyTo(_data.AsSpan(dst, _channels));
        }

        End += frames;
        return frames;
    }

    public float Get(long frame, int channel)
    {
        if (frame < Start)
        {
            frame = Start;
        }
        else if (frame >= End)
        {
            frame = End - 1;
        }

        return _data[((int)(frame & (_capacity - 1)) * _channels) + channel];
    }

    public void DropBefore(long frame)
    {
        if (frame > End)
        {
            frame = End;
        }

        if (frame > Start)
        {
            Start = frame;
        }
    }

    public void Clear()
    {
        Start = 0;
        End = 0;
    }

    private void EnsureCapacity(long frames)
    {
        if (frames <= _capacity)
        {
            return;
        }

        var newCap = _capacity;
        while (newCap < frames)
        {
            newCap <<= 1;
        }

        var newData = new float[newCap * _channels];
        var oldMask = _capacity - 1;
        var newMask = newCap - 1;
        for (var f = Start; f < End; f++)
        {
            Array.Copy(_data, (int)(f & oldMask) * _channels, newData, (int)(f & newMask) * _channels, _channels);
        }

        _data = newData;
        _capacity = newCap;
    }
}
