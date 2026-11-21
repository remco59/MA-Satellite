namespace MaSatellite.Core.Audio;

/// <summary>
/// Mixes short announcement clips into the live player output without stopping the Sendspin stream.
/// The audio callback does no decoding or I/O: clips are decoded and resampled before Enqueue is called.
/// </summary>
public sealed class AnnouncementMixer
{
    private const float DuckGain = 0.24f;
    private const float FadeSeconds = 0.12f;
    private const int MaxQueuedSeconds = 180;

    private readonly object _gate = new();
    private readonly Queue<float[]> _pending = new();
    private float[]? _current;
    private int _sampleOffset;
    private int _sampleRate = 48000;
    private int _channels = 2;
    private float _musicGain = 1f;
    private long _queuedSamples;

    public AudioOutputFormat OutputFormat
    {
        get
        {
            lock (_gate)
            {
                return new AudioOutputFormat(_sampleRate, _channels);
            }
        }
    }

    /// <summary>Called whenever WASAPI starts or changes output device.</summary>
    public void SetOutputFormat(AudioOutputFormat format)
    {
        if (format.SampleRate <= 0 || format.Channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        lock (_gate)
        {
            if (_sampleRate == format.SampleRate && _channels == format.Channels)
            {
                return;
            }

            _sampleRate = format.SampleRate;
            _channels = format.Channels;
            _pending.Clear();
            _current = null;
            _sampleOffset = 0;
            _musicGain = 1f;
            _queuedSamples = 0;
        }
    }

    /// <summary>
    /// Queue interleaved float PCM already converted to the current output sample rate and channel count.
    /// Multiple announcements play in arrival order rather than cutting one another off.
    /// </summary>
    public void Enqueue(ReadOnlySpan<float> samples, int sampleRate, int channels, int volumeLevel = 100)
    {
        if (samples.IsEmpty)
        {
            throw new ArgumentException("Announcement audio is empty.", nameof(samples));
        }

        lock (_gate)
        {
            if (sampleRate != _sampleRate || channels != _channels || samples.Length % channels != 0)
            {
                throw new ArgumentException("Announcement PCM must match the active output format and contain complete frames.");
            }

            var clip = samples.ToArray();
            if (_queuedSamples + clip.Length > (long)_sampleRate * _channels * MaxQueuedSeconds)
            {
                throw new InvalidOperationException($"The announcement queue is limited to {MaxQueuedSeconds} seconds.");
            }
            var gain = Math.Clamp(volumeLevel, 0, 100) / 100f;
            for (var i = 0; i < clip.Length; i++)
            {
                clip[i] *= gain;
            }

            _pending.Enqueue(clip);
            _queuedSamples += clip.Length;
        }
    }

    /// <summary>Apply ducking and add the next announcement block to the music buffer.</summary>
    public void Mix(Span<float> buffer)
    {
        lock (_gate)
        {
            var frames = buffer.Length / _channels;
            var gainStep = (1f - DuckGain) / Math.Max(1f, _sampleRate * FadeSeconds);

            for (var frame = 0; frame < frames; frame++)
            {
                EnsureCurrentLocked();
                var hasAnnouncement = _current is not null;
                var targetGain = hasAnnouncement ? DuckGain : 1f;
                _musicGain = MoveTowards(_musicGain, targetGain, gainStep);

                var baseIndex = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                {
                    var announcementSample = hasAnnouncement ? _current![_sampleOffset + channel] : 0f;
                    buffer[baseIndex + channel] = Math.Clamp(
                        (buffer[baseIndex + channel] * _musicGain) + announcementSample,
                        -1f,
                        1f);
                }

                if (hasAnnouncement)
                {
                    _sampleOffset += _channels;
                    _queuedSamples -= _channels;
                    if (_sampleOffset >= _current!.Length)
                    {
                        _current = null;
                        _sampleOffset = 0;
                    }
                }
            }
        }
    }

    /// <summary>Discard queued announcements, used when the Sendspin session ends.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _current = null;
            _sampleOffset = 0;
            _musicGain = 1f;
            _queuedSamples = 0;
        }
    }

    private void EnsureCurrentLocked()
    {
        if (_current is null && _pending.TryDequeue(out var next))
        {
            _current = next;
            _sampleOffset = 0;
        }
    }

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        if (Math.Abs(target - current) <= maxDelta)
        {
            return target;
        }

        return current + Math.Sign(target - current) * maxDelta;
    }
}
