using MaSatellite.Core.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Core.Audio;

public sealed record PlaybackOptions
{
    /// <summary>Errors below this are ignored (µs).</summary>
    public double DeadbandMicros { get; init; } = 1_000;

    /// <summary>Errors above this are fixed by skipping audio or inserting silence instead of resampling (µs).</summary>
    public double HardThresholdMicros { get; init; } = 30_000;

    /// <summary>Soft corrections aim to remove the error over this many seconds.</summary>
    public double CorrectionSeconds { get; init; } = 2.0;

    /// <summary>Largest resampling-ratio change used for soft correction (0.005 = 0.5 %).</summary>
    public double MaxAdjust { get; init; } = 0.005;

    /// <summary>Exponential smoothing factor applied to the measured error per fill.</summary>
    public double SmoothingAlpha { get; init; } = 0.1;

    /// <summary>The smoothed error must stay within this to be reported in sync (µs).</summary>
    public double InSyncMicros { get; init; } = 10_000;

    public int MaxBufferSeconds { get; init; } = 60;
}

public readonly record struct PlaybackStats(
    double BufferedMs,
    double SyncErrorMicros,
    bool InSync,
    bool Playing,
    long Underruns,
    long FramesSkipped,
    long FramesInserted);

/// <summary>
/// Turns timestamped decoded audio into device-rate output that plays at the right moment: it waits for the start
/// time, resamples to the device rate, trims small drift with a slightly varied resampling ratio and fixes large
/// errors by skipping audio or inserting silence. <see cref="Fill"/> runs on the audio thread.
/// </summary>
public sealed class PlaybackEngine
{
    private readonly IClockMapper _clock;
    private readonly ILogger _logger;
    private readonly PlaybackOptions _options;
    private readonly object _gate = new();
    private readonly List<(long StartFrame, long TimestampMicros)> _segments = [];

    private FrameRing? _ring;
    private int _srcRate;
    private int _srcChannels;
    private int _outRate;
    private int _outChannels;
    private double _readPos; // absolute source frame, fractional
    private bool _playing;
    private double _smoothedError;
    private bool _haveSmoothed;
    private bool _inSync;
    private long _underruns;
    private long _skipped;
    private long _inserted;
    private double _lastError;
    private int _staticDelayMs;

    public PlaybackEngine(IClockMapper clock, ILogger? logger = null, PlaybackOptions? options = null)
    {
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? new PlaybackOptions();
    }

    /// <summary>Raised (outside the audio lock) when the engine gains or loses synchronisation.</summary>
    public event Action<bool>? InSyncChanged;

    public int StaticDelayMs
    {
        get => _staticDelayMs;
        set => _staticDelayMs = Math.Clamp(value, 0, 5000);
    }

    public PlaybackStats Stats
    {
        get
        {
            lock (_gate)
            {
                var buffered = _ring is null || _srcRate == 0 ? 0 : Math.Max(_ring.End - _readPos, 0) * 1000.0 / _srcRate;
                return new PlaybackStats(buffered, _lastError, _inSync, _playing, _underruns, _skipped, _inserted);
            }
        }
    }

    /// <summary>Sets source and output formats and discards buffered audio.</summary>
    public void Configure(AudioStreamFormat source, int outputSampleRate, int outputChannels)
    {
        lock (_gate)
        {
            _srcRate = source.SampleRate;
            _srcChannels = source.Channels;
            _outRate = outputSampleRate;
            _outChannels = outputChannels;
            _ring = new FrameRing(source.Channels, source.SampleRate / 2, (long)source.SampleRate * _options.MaxBufferSeconds);
            ResetLocked();
        }
    }

    public void Clear()
    {
        bool changed;
        lock (_gate)
        {
            ResetLocked();
            changed = SetInSyncLocked(false);
        }

        if (changed)
        {
            InSyncChanged?.Invoke(false);
        }
    }

    public void Enqueue(long serverTimestampMicros, ReadOnlySpan<float> interleaved)
    {
        lock (_gate)
        {
            if (_ring is null || interleaved.Length < _srcChannels)
            {
                return;
            }

            var startFrame = _ring.End;
            var stored = _ring.Append(interleaved);
            if (stored == 0)
            {
                _logger.LogWarning("Playback buffer full; dropping chunk.");
                return;
            }

            _segments.Add((startFrame, serverTimestampMicros));
            if (_ring.Count == stored)
            {
                _readPos = startFrame; // first data after a reset
            }
        }
    }

    /// <summary>Fills <paramref name="output"/> (interleaved, device channels). <paramref name="playoutLocalMicros"/> is when its first frame is audible.</summary>
    public void Fill(Span<float> output, long playoutLocalMicros)
    {
        bool? syncChange = null;
        lock (_gate)
        {
            output.Clear();
            if (_ring is null || _outChannels == 0 || _outRate == 0)
            {
                return;
            }

            var frames = output.Length / _outChannels;
            var starved = _ring.End - 1 <= (long)Math.Floor(_readPos);
            if (starved || _segments.Count == 0 || !_clock.IsSynchronized)
            {
                if (_playing && starved)
                {
                    _underruns++;
                    _playing = false;
                    _haveSmoothed = false;
                    if (SetInSyncLocked(false))
                    {
                        syncChange = false;
                    }
                }

                goto Done;
            }

            var wantTs = _clock.LocalToServer(playoutLocalMicros + (_staticDelayMs * 1000L));
            var headTs = TimestampAt(_readPos);
            var error = headTs - wantTs; // > 0: head is for later (we are early); < 0: we are late
            _lastError = error;
            var outIndex = 0;

            if (_playing && error > _options.HardThresholdMicros)
            {
                // Far too early (a hole in the timeline): wait for the exact time like at startup instead of crawling.
                _playing = false;
                if (SetInSyncLocked(false))
                {
                    syncChange = false;
                }
            }

            if (!_playing)
            {
                if (error > 0)
                {
                    var silenceFrames = error * _outRate / 1_000_000.0;
                    if (silenceFrames >= frames)
                    {
                        _inserted += frames;
                        goto Done; // still waiting for the start time
                    }

                    outIndex = (int)Math.Round(silenceFrames);
                    _inserted += outIndex;
                }
                else if (error < -_options.DeadbandMicros)
                {
                    SkipSource(-error);
                }

                _playing = true;
                _haveSmoothed = false;
            }
            else if (error < -_options.HardThresholdMicros)
            {
                SkipSource(-error);
                _haveSmoothed = false;
                if (SetInSyncLocked(false))
                {
                    syncChange = false;
                }
            }

            var adj = 1.0;
            if (_playing && Math.Abs(error) <= _options.HardThresholdMicros)
            {
                _smoothedError = _haveSmoothed ? _smoothedError + (_options.SmoothingAlpha * (error - _smoothedError)) : error;
                _haveSmoothed = true;
                if (Math.Abs(_smoothedError) > _options.DeadbandMicros)
                {
                    adj = 1.0 - Math.Clamp(_smoothedError / (_options.CorrectionSeconds * 1_000_000.0), -_options.MaxAdjust, _options.MaxAdjust);
                }

                if (Math.Abs(_smoothedError) <= _options.InSyncMicros && SetInSyncLocked(true))
                {
                    syncChange = true;
                }
                else if (Math.Abs(_smoothedError) > _options.InSyncMicros * 2 && SetInSyncLocked(false))
                {
                    syncChange = false;
                }
            }

            if (RenderResampled(output, outIndex, frames, (double)_srcRate / _outRate * adj) && SetInSyncLocked(false))
            {
                syncChange = false;
            }
        }

    Done:
        if (syncChange is { } v)
        {
            InSyncChanged?.Invoke(v);
        }
    }

    /// <summary>Renders into the output; returns true if the source ran dry before the buffer was full.</summary>
    private bool RenderResampled(Span<float> output, int outIndex, int frames, double step)
    {
        var ring = _ring!;
        var underrun = false;
        for (; outIndex < frames; outIndex++)
        {
            var i1 = (long)Math.Floor(_readPos);
            if (i1 >= ring.End - 1)
            {
                // Ran out of source audio mid-buffer.
                _underruns++;
                _playing = false;
                _haveSmoothed = false;
                _readPos = Math.Max(_readPos, ring.End);
                underrun = true;
                break;
            }

            var t = (float)(_readPos - i1);
            WriteFrame(output, outIndex, ring, i1, t);
            _readPos += step;
        }

        var free = (long)Math.Floor(_readPos) - 2;
        if (free > ring.Start)
        {
            ring.DropBefore(free);
            var firstKeep = 0;
            while (firstKeep + 1 < _segments.Count && _segments[firstKeep + 1].StartFrame <= ring.Start)
            {
                firstKeep++;
            }

            if (firstKeep > 0)
            {
                _segments.RemoveRange(0, firstKeep);
            }
        }

        return underrun;
    }

    private void WriteFrame(Span<float> output, int outIndex, FrameRing ring, long i1, float t)
    {
        var baseOut = outIndex * _outChannels;
        for (var oc = 0; oc < _outChannels; oc++)
        {
            int sc;
            if (_srcChannels == 1)
            {
                sc = 0;
            }
            else if (oc < _srcChannels && oc < 2 || (_outChannels <= _srcChannels && oc < _srcChannels))
            {
                sc = oc;
            }
            else
            {
                continue; // extra output channels (surround speakers) stay silent
            }

            var y0 = ring.Get(i1 - 1, sc);
            var y1 = ring.Get(i1, sc);
            var y2 = ring.Get(i1 + 1, sc);
            var y3 = ring.Get(i1 + 2, sc);
            output[baseOut + oc] = Hermite(y0, y1, y2, y3, t);
        }
    }

    private static float Hermite(float y0, float y1, float y2, float y3, float t)
    {
        if (t == 0f)
        {
            return y1;
        }

        var c0 = y1;
        var c1 = 0.5f * (y2 - y0);
        var c2 = y0 - (2.5f * y1) + (2f * y2) - (0.5f * y3);
        var c3 = (0.5f * (y3 - y0)) + (1.5f * (y1 - y2));
        return (((((c3 * t) + c2) * t) + c1) * t) + c0;
    }

    private void SkipSource(double micros)
    {
        var ring = _ring!;
        var frames = micros * _srcRate / 1_000_000.0;
        var target = Math.Min(_readPos + frames, ring.End);
        _skipped += (long)(target - _readPos);
        _readPos = target;
        var free = (long)Math.Floor(_readPos) - 2;
        if (free > ring.Start)
        {
            ring.DropBefore(free);
        }
    }

    /// <summary>Server timestamp (µs) of a possibly fractional absolute source frame.</summary>
    private double TimestampAt(double framePos)
    {
        var idx = 0;
        // Segments are few and sorted; search from the back because the read position tracks the newest segments.
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            if (_segments[i].StartFrame <= framePos)
            {
                idx = i;
                break;
            }
        }

        var seg = _segments[idx];
        return seg.TimestampMicros + ((framePos - seg.StartFrame) * 1_000_000.0 / _srcRate);
    }

    private void ResetLocked()
    {
        _ring?.Clear();
        _segments.Clear();
        _readPos = 0;
        _playing = false;
        _haveSmoothed = false;
        _smoothedError = 0;
        _lastError = 0;
    }

    private bool SetInSyncLocked(bool value)
    {
        if (_inSync == value)
        {
            return false;
        }

        _inSync = value;
        return true;
    }
}
