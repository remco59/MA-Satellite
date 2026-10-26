using MaSatellite.Core.Audio;
using MaSatellite.Core.Discovery;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Time;
using MaSatellite.Core.Transport;
using MaSatellite.Core.Volume;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Core.Session;

/// <summary>
/// Runs the player: advertises and listens (or connects out), keeps at most one server connected, reconnects with
/// backoff and exposes one aggregated <see cref="SatelliteStatus"/>.
/// </summary>
public sealed class SendspinService : IAsyncDisposable
{
    private readonly IAudioOutput _output;
    private readonly IVolumeControl _volume;
    private readonly ILocalClock _localClock;
    private readonly IServiceAdvertiser? _advertiser;
    private readonly ILastServerStore _lastServer;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly ClockSync _clock;
    private readonly PlaybackEngine _engine;
    private readonly PlayerPipeline _pipeline;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _arbitration = new(1, 1);

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private SendspinListener? _listener;
    private IDisposable? _advertisement;
    private SendspinSession? _current;
    private SendspinOptions _options = new();
    private ServerConnectionOptions _connection = new();
    private SatelliteStatus _status = new();
    private bool _inSync;
    private bool _disposed;

    public SendspinService(
        IAudioOutput output,
        IVolumeControl volume,
        ILocalClock? localClock = null,
        IServiceAdvertiser? advertiser = null,
        ILastServerStore? lastServerStore = null,
        ILoggerFactory? loggerFactory = null)
    {
        _output = output;
        _volume = volume;
        _localClock = localClock ?? StopwatchClock.Instance;
        _advertiser = advertiser;
        _lastServer = lastServerStore ?? new InMemoryLastServerStore();
        _loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<SendspinService>();
        _clock = new ClockSync(_localClock);
        _engine = new PlaybackEngine(_clock, _loggers.CreateLogger<PlaybackEngine>());
        _pipeline = new PlayerPipeline(output, _engine, _loggers.CreateLogger<PlayerPipeline>());
        _pipeline.InSyncChanged += v =>
        {
            _inSync = v;
            Publish(s => s with { InSync = v });
        };
        _volume.Changed += OnVolumeChanged;
        _status = _status with { Volume = volume.Volume, Muted = volume.Muted };
    }

    public SatelliteStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public PlaybackStats Playback => _pipeline.Stats;

    /// <summary>The port the listener is bound to (Advertise mode), once started.</summary>
    public int? ListenPort => _listener?.Port;

    public event Action<SatelliteStatus>? StatusChanged;

    /// <summary>Raised when the server changes the static delay (so the host can persist it).</summary>
    public event Action<int>? StaticDelayChanged;

    public Task StartAsync(SendspinOptions options, ServerConnectionOptions connection)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runCts is not null)
            {
                throw new InvalidOperationException("Already started.");
            }

            _options = options;
            _connection = connection;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            _runTask = connection.Mode == ConnectionMode.Advertise ? RunListenerAsync(ct) : RunConnectorAsync(ct);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? run;
        SendspinSession? current;
        lock (_gate)
        {
            cts = _runCts;
            run = _runTask;
            current = _current;
            _runCts = null;
            _runTask = null;
        }

        if (cts is null)
        {
            return;
        }

        if (current is not null)
        {
            await current.DisconnectAsync(GoodbyeReasons.Shutdown).ConfigureAwait(false);
        }

        await cts.CancelAsync().ConfigureAwait(false);
        if (run is not null)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
        Publish(s => s with { Connection = ConnectionState.Stopped, Detail = null, Session = new SessionState() });
    }

    /// <summary>Stops and starts again with new options (settings changed).</summary>
    public async Task RestartAsync(SendspinOptions options, ServerConnectionOptions connection)
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync(options, connection).ConfigureAwait(false);
    }

    /// <summary>Changes the static delay without reconnecting.</summary>
    public async Task SetStaticDelayAsync(int delayMs)
    {
        lock (_gate)
        {
            _options = _options with { StaticDelayMs = delayMs };
        }

        _pipeline.StaticDelayMs = delayMs;
        var session = _current;
        if (session is not null)
        {
            await session.SetStaticDelayAsync(delayMs).ConfigureAwait(false);
        }
    }

    public Task SendCommandAsync(ControllerCommand command, CancellationToken cancellationToken = default)
    {
        var session = _current;
        return session is null ? Task.CompletedTask : session.SendCommandAsync(command, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _volume.Changed -= OnVolumeChanged;
        _pipeline.Dispose();
        _arbitration.Dispose();
    }

    private async Task RunListenerAsync(CancellationToken ct)
    {
        try
        {
            _listener = new SendspinListener(_connection.ListenPort, _connection.Path, HandleTransportAsync, _localClock, _loggers.CreateLogger<SendspinListener>());
            _listener.Start();
            if (_advertiser is not null)
            {
                try
                {
                    _advertisement = _advertiser.Advertise(_options.PlayerName, _listener.Port, _connection.Path, _options.PlayerName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "mDNS advertising failed; add this PC's IP address manually in Music Assistant");
                    Publish(s => s with { Detail = "mDNS advertising failed" });
                }
            }

            Publish(s => s with { Connection = ConnectionState.Listening, Detail = $"Listening on port {_listener.Port}" });
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start the Sendspin listener on port {Port}", _connection.ListenPort);
            Publish(s => s with { Connection = ConnectionState.Stopped, Detail = $"Cannot listen on port {_connection.ListenPort}: {ex.Message}" });
        }
        finally
        {
            _advertisement?.Dispose();
            _advertisement = null;
            if (_listener is not null)
            {
                await _listener.DisposeAsync().ConfigureAwait(false);
                _listener = null;
            }
        }
    }

    private async Task RunConnectorAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Publish(s => s with { Connection = ConnectionState.Connecting, Detail = $"Connecting to {_connection.ServerHost}:{_connection.ServerPort}" });
                var transport = await SendspinConnector.ConnectAsync(_connection.ServerHost, _connection.ServerPort, _connection.Path, _localClock, ct).ConfigureAwait(false);
                var started = _localClock.NowMicros;
                await HandleTransportAsync(transport).ConfigureAwait(false);
                if (_localClock.NowMicros - started > 10_000_000)
                {
                    backoff = TimeSpan.FromSeconds(1);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Cannot reach {Host}:{Port}: {Message}", _connection.ServerHost, _connection.ServerPort, ex.Message);
            }

            Publish(s => s with { Connection = ConnectionState.Connecting, Detail = $"Retrying in {backoff.TotalSeconds:0} s" });
            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    /// <summary>Owns one connection from the transport being available until it closes.</summary>
    private async Task HandleTransportAsync(ISendspinTransport transport)
    {
        var ct = _runCts?.Token ?? CancellationToken.None;
        await using var session = new SendspinSession(transport, _options, _clock, _pipeline, _volume, _localClock, _loggers.CreateLogger<SendspinSession>());
        SessionState? last = null;
        session.AcceptServerAsync = hello => AcceptAsync(session, hello);
        session.StaticDelayChanged += d => StaticDelayChanged?.Invoke(d);
        session.StateChanged += state =>
        {
            if (_current != session)
            {
                return;
            }

            if (state.Group.PlaybackState == GroupPlaybackState.Playing && last?.Group.PlaybackState != GroupPlaybackState.Playing && state.ServerId is { } id)
            {
                _lastServer.LastPlayedServerId = id;
            }

            last = state;
            Publish(s => s with { Session = state });
        };

        try
        {
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            var wasCurrent = false;
            lock (_gate)
            {
                if (_current == session)
                {
                    _current = null;
                    wasCurrent = true;
                }
            }

            if (wasCurrent)
            {
                _pipeline.EndStream();
                var connection = _connection.Mode == ConnectionMode.Advertise ? ConnectionState.Listening : ConnectionState.Connecting;
                Publish(s => s with { Connection = connection, Session = new SessionState(), Detail = null, InSync = false });
            }
        }
    }

    private async Task<bool> AcceptAsync(SendspinSession candidate, ServerHello hello)
    {
        await _arbitration.WaitAsync().ConfigureAwait(false);
        try
        {
            SendspinSession? existing;
            lock (_gate)
            {
                existing = _current;
            }

            if (existing is not null)
            {
                var existingReason = existing.State.ConnectionReason;
                var takeOver = hello.ConnectionReason == ConnectionReasons.Playback
                    || (existingReason != ConnectionReasons.Playback && hello.ServerId == _lastServer.LastPlayedServerId && existing.State.ServerId != _lastServer.LastPlayedServerId);
                if (!takeOver)
                {
                    return false;
                }

                _logger.LogInformation("Switching from {Old} to {New}", existing.State.ServerName, hello.Name);
                lock (_gate)
                {
                    _current = candidate; // the old session must not clean up shared state any more
                }

                await existing.DisconnectAsync(GoodbyeReasons.AnotherServer).ConfigureAwait(false);
                _pipeline.EndStream();
            }
            else
            {
                lock (_gate)
                {
                    _current = candidate;
                }
            }

            Publish(s => s with { Connection = ConnectionState.Connected, Detail = null, Session = candidate.State });
            return true;
        }
        finally
        {
            _arbitration.Release();
        }
    }

    private void OnVolumeChanged() => Publish(s => s with { Volume = _volume.Volume, Muted = _volume.Muted });

    private void Publish(Func<SatelliteStatus, SatelliteStatus> update)
    {
        SatelliteStatus snapshot;
        lock (_gate)
        {
            _status = update(_status) with { Volume = _volume.Volume, Muted = _volume.Muted };
            snapshot = _status;
        }

        StatusChanged?.Invoke(snapshot);
    }
}
