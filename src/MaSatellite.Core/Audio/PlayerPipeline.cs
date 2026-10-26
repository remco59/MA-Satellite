using MaSatellite.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Core.Audio;

/// <summary>Decoder → <see cref="PlaybackEngine"/> → output device, with output restart when the device fails.</summary>
public sealed class PlayerPipeline : IDisposable
{
    private readonly IAudioOutput _output;
    private readonly PlaybackEngine _engine;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private IAudioDecoder? _decoder;
    private StreamPlayerFormat? _format;
    private bool _outputRunning;
    private AudioOutputFormat _outputFormat;
    private long _decodeErrors;
    private bool _disposed;

    public PlayerPipeline(IAudioOutput output, PlaybackEngine engine, ILogger? logger = null)
    {
        _output = output;
        _engine = engine;
        _logger = logger ?? NullLogger.Instance;
        _output.Failed += OnOutputFailed;
        _engine.InSyncChanged += v => InSyncChanged?.Invoke(v);
    }

    public event Action<bool>? InSyncChanged;

    /// <summary>Raised when the output could not be (re)started; the stream continues to be buffered meanwhile.</summary>
    public event Action<Exception>? OutputError;

    public int StaticDelayMs
    {
        get => _engine.StaticDelayMs;
        set => _engine.StaticDelayMs = value;
    }

    public PlaybackStats Stats => _engine.Stats;

    public long DecodeErrors => Interlocked.Read(ref _decodeErrors);

    public bool StreamActive
    {
        get
        {
            lock (_gate)
            {
                return _decoder is not null;
            }
        }
    }

    public AudioOutputFormat? OutputFormat
    {
        get
        {
            lock (_gate)
            {
                return _outputRunning ? _outputFormat : null;
            }
        }
    }

    /// <summary>Starts (or reconfigures) playback for a new stream format. Throws <see cref="AudioDecodeException"/> for unsupported formats.</summary>
    public void StartStream(StreamPlayerFormat format)
    {
        var decoder = AudioDecoderFactory.Create(format);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _decoder = decoder;
            _format = format;
            EnsureOutputLocked();
            _engine.Configure(decoder.Format, _outputFormat.SampleRate, _outputFormat.Channels);
        }
    }

    public void OnAudioChunk(long serverTimestampMicros, ReadOnlyMemory<byte> data)
    {
        IAudioDecoder? decoder;
        lock (_gate)
        {
            decoder = _decoder;
        }

        if (decoder is null || data.IsEmpty)
        {
            return;
        }

        try
        {
            var samples = decoder.Decode(data.Span);
            _engine.Enqueue(serverTimestampMicros, samples);
        }
        catch (AudioDecodeException ex)
        {
            var n = Interlocked.Increment(ref _decodeErrors);
            if (n <= 5 || n % 100 == 0)
            {
                _logger.LogWarning(ex, "Dropped an undecodable audio chunk ({Count} so far)", n);
            }
        }
    }

    public void Clear() => _engine.Clear();

    public void EndStream()
    {
        lock (_gate)
        {
            _decoder = null;
            _format = null;
            StopOutputLocked();
        }

        _engine.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _decoder = null;
            StopOutputLocked();
        }

        _output.Failed -= OnOutputFailed;
        _output.Dispose();
    }

    private void EnsureOutputLocked()
    {
        if (_outputRunning)
        {
            return;
        }

        _outputFormat = _output.Start(_engine.Fill);
        _outputRunning = true;
        _logger.LogInformation("Audio output started: {Rate} Hz, {Channels} ch", _outputFormat.SampleRate, _outputFormat.Channels);
    }

    private void StopOutputLocked()
    {
        if (!_outputRunning)
        {
            return;
        }

        _outputRunning = false;
        try
        {
            _output.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping the audio output failed");
        }
    }

    private void OnOutputFailed(object? sender, Exception? error)
    {
        _logger.LogWarning(error, "Audio output failed; restarting");
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1))).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || _decoder is null)
                    {
                        return;
                    }

                    try
                    {
                        StopOutputLocked();
                        EnsureOutputLocked();
                        if (_decoder is { } d)
                        {
                            _engine.Configure(d.Format, _outputFormat.SampleRate, _outputFormat.Channels);
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Audio output restart attempt {Attempt} failed", attempt + 1);
                        OutputError?.Invoke(ex);
                    }
                }
            }
        });
    }
}
