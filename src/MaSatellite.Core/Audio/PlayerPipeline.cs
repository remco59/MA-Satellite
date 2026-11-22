using MaSatellite.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Core.Audio;

/// <summary>Decoder → <see cref="PlaybackEngine"/> → output device, with output restart when the device fails.</summary>
public sealed class PlayerPipeline : IDisposable
{
    private static readonly TimeSpan OutputChangeDebounce = TimeSpan.FromMilliseconds(250);
    private readonly IAudioOutput _output;
    private readonly PlaybackEngine _engine;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private IAudioDecoder? _decoder;
    private StreamPlayerFormat? _format;
    private bool _outputRunning;
    private AudioOutputFormat _outputFormat;
    private long _decodeErrors;
    private long _outputFailureVersion;
    private int _restartScheduled;
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
        get { lock (_gate) { return _decoder is not null; } }
    }

    public AudioOutputFormat? OutputFormat
    {
        get { lock (_gate) { return _outputRunning ? _outputFormat : null; } }
    }

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
        lock (_gate) { decoder = _decoder; }
        if (decoder is null || data.IsEmpty) return;

        try
        {
            var samples = decoder.Decode(data.Span);
            _engine.Enqueue(serverTimestampMicros, samples);
        }
        catch (AudioDecodeException ex)
        {
            var n = Interlocked.Increment(ref _decodeErrors);
            if (n <= 5 || n % 100 == 0)
                _logger.LogWarning(ex, "Dropped an undecodable audio chunk ({Count} so far)", n);
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
            if (_disposed) return;
            _disposed = true;
            _decoder = null;
            StopOutputLocked();
        }
        _output.Failed -= OnOutputFailed;
        _output.Dispose();
    }

    private void EnsureOutputLocked()
    {
        if (_outputRunning) return;
        _outputFormat = _output.Start(_engine.Fill);
        _outputRunning = true;
        _logger.LogInformation("Audio output started: {Rate} Hz, {Channels} ch", _outputFormat.SampleRate, _outputFormat.Channels);
    }

    private void StopOutputLocked()
    {
        // Stop is deliberately idempotent: a previous stop can time out while the WASAPI
        // render thread is still unwinding. Retry it before attempting to start a new client.
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
        Interlocked.Increment(ref _outputFailureVersion);
        _logger.LogWarning(error, "Audio output failed or the Windows output device changed");
        if (Interlocked.CompareExchange(ref _restartScheduled, 1, 0) == 0)
            _ = Task.Run(RestartOutputAsync);
    }

    private async Task RestartOutputAsync()
    {
        var attempt = 0;
        var handledVersion = Volatile.Read(ref _outputFailureVersion);
        try
        {
            while (attempt < 10)
            {
                // Windows can emit several endpoint notifications for one user action.
                // Wait until the notifications settle, then perform one restart.
                var version = Volatile.Read(ref _outputFailureVersion);
                await Task.Delay(OutputChangeDebounce).ConfigureAwait(false);
                if (version != Volatile.Read(ref _outputFailureVersion))
                    continue;

                lock (_gate)
                {
                    if (_disposed || _decoder is null) return;
                    try
                    {
                        StopOutputLocked();
                        EnsureOutputLocked();
                        if (_decoder is { } decoder)
                            _engine.Configure(decoder.Format, _outputFormat.SampleRate, _outputFormat.Channels);
                    }
                    catch (Exception ex)
                    {
                        attempt++;
                        _logger.LogWarning(ex, "Audio output restart attempt {Attempt} failed", attempt);
                        OutputError?.Invoke(ex);
                        continue;
                    }
                }

                handledVersion = version;
                // If another endpoint change arrived during restart, process the newest endpoint too.
                if (version == Volatile.Read(ref _outputFailureVersion))
                    return;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _restartScheduled, 0);
            // Close the race where a notification arrived just as this worker was exiting.
            if (!_disposed && _decoder is not null &&
                Volatile.Read(ref _outputFailureVersion) != handledVersion &&
                Interlocked.CompareExchange(ref _restartScheduled, 1, 0) == 0)
                _ = Task.Run(RestartOutputAsync);
        }
    }
}
