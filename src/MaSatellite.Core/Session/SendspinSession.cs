using MaSatellite.Core.Audio;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Time;
using MaSatellite.Core.Transport;
using MaSatellite.Core.Volume;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Core.Session;

/// <summary>One Sendspin connection: handshake, clock sync, stream handling and state tracking.</summary>
public sealed class SendspinSession : IAsyncDisposable
{
    private readonly ISendspinTransport _transport;
    private readonly SendspinOptions _options;
    private readonly ClockSync _clock;
    private readonly PlayerPipeline _pipeline;
    private readonly IVolumeControl _volume;
    private readonly ILocalClock _localClock;
    private readonly ILogger _logger;
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private SessionState _state = new();
    private int _staticDelayMs;
    private string _reportedState = ClientStates.Synchronized;
    private bool _wasInSync;
    private CancellationTokenSource? _syncDebounce;
    private int _artworkVersion;
    private bool _helloDone;
    private bool _disposed;
    private Task? _timeTask;

    public SendspinSession(
        ISendspinTransport transport,
        SendspinOptions options,
        ClockSync clock,
        PlayerPipeline pipeline,
        IVolumeControl volume,
        ILocalClock localClock,
        ILogger? logger = null)
    {
        _transport = transport;
        _options = options;
        _clock = clock;
        _pipeline = pipeline;
        _volume = volume;
        _localClock = localClock;
        _logger = logger ?? NullLogger.Instance;
        _staticDelayMs = options.StaticDelayMs;
    }

    public SessionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public string Remote => _transport.RemoteDescription;

    /// <summary>Called once with the server's hello. Return false to decline (another server is preferred); we then say goodbye.</summary>
    public Func<ServerHello, Task<bool>>? AcceptServerAsync { get; set; }

    public event Action<SessionState>? StateChanged;

    /// <summary>
    /// Raised when the server answers with the newer encrypted Sendspin handshake (<c>server/init</c>, <c>noise/handshake</c>,
    /// <c>server/error</c>) instead of <c>server/hello</c>: it no longer accepts plaintext (legacy) clients.
    /// </summary>
    public event Action<string>? ProtocolMismatch;

    /// <summary>Raised when the server changed our static delay; the host should persist it.</summary>
    public event Action<int>? StaticDelayChanged;

    /// <summary>Runs the connection until it closes. Completes normally on a clean close.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var ct = linked.Token;
        _pipeline.StaticDelayMs = _staticDelayMs;
        _pipeline.InSyncChanged += OnPipelineSyncChanged;
        _volume.Changed += OnVolumeChanged;

        try
        {
            await _transport.SendTextAsync(MessageCodec.ClientHello(BuildHello()), ct).ConfigureAwait(false);
            _ = HelloTimeoutAsync(linked);

            await foreach (var message in _transport.ReceiveAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (message.IsText)
                    {
                        var proceed = await HandleTextAsync(message, ct).ConfigureAwait(false);
                        if (!proceed)
                        {
                            break;
                        }
                    }
                    else
                    {
                        HandleBinary(message);
                    }
                }
                catch (FormatException ex)
                {
                    _logger.LogWarning(ex, "Ignoring malformed message from {Remote}", Remote);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _pipeline.InSyncChanged -= OnPipelineSyncChanged;
            _volume.Changed -= OnVolumeChanged;
            await linked.CancelAsync().ConfigureAwait(false);
            if (_timeTask is not null)
            {
                try
                {
                    await _timeTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                }
            }

            _syncDebounce?.Cancel();
            _logger.LogInformation("Session with {Remote} ended", Remote);
        }
    }

    public async Task SendCommandAsync(ControllerCommand command, CancellationToken cancellationToken = default)
    {
        if (!_helloDone)
        {
            return;
        }

        var controller = State.Controller;
        if (controller.SupportedCommands.Count > 0 && !controller.Supports(command.Command))
        {
            _logger.LogDebug("Server does not support command {Command}", command.Command);
            return;
        }

        await _transport.SendTextAsync(MessageCodec.ClientCommand(command), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies a delay chosen locally (settings UI) and tells the server.</summary>
    public async Task SetStaticDelayAsync(int delayMs, CancellationToken cancellationToken = default)
    {
        _staticDelayMs = Math.Clamp(delayMs, 0, 5000);
        _pipeline.StaticDelayMs = _staticDelayMs;
        if (_helloDone)
        {
            await SendStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Tells the server why we leave, then closes. Safe to call more than once.</summary>
    public async Task DisconnectAsync(string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _transport.SendTextAsync(MessageCodec.ClientGoodbye(reason), timeout.Token).ConfigureAwait(false);
            await _transport.CloseAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException or IOException)
        {
        }
        finally
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _syncDebounce?.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private ClientHello BuildHello()
    {
        var roles = new List<string> { Roles.Player, Roles.Controller, Roles.Metadata };
        ArtworkSupport? artwork = null;
        if (_options.RequestArtwork)
        {
            roles.Add(Roles.Artwork);
            artwork = new ArtworkSupport([new ArtworkChannelSpec("album", "jpeg", _options.ArtworkSize, _options.ArtworkSize)]);
        }

        return new ClientHello(
            _options.ClientId,
            _options.PlayerName,
            new DeviceInfo("ma-satellite", "ma-satellite", _options.SoftwareVersion),
            1,
            roles,
            new PlayerSupport(_options.SupportedFormats, _options.BufferCapacityBytes, ["volume", "mute"]),
            artwork);
    }

    private async Task HelloTimeoutAsync(CancellationTokenSource linked)
    {
        try
        {
            await Task.Delay(_options.HelloTimeout, linked.Token).ConfigureAwait(false);
            if (!_helloDone)
            {
                _logger.LogWarning("No server/hello from {Remote} within {Timeout}", Remote, _options.HelloTimeout);
                await linked.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <returns>false to stop the receive loop.</returns>
    private async Task<bool> HandleTextAsync(TransportMessage message, CancellationToken ct)
    {
        var parsed = MessageCodec.ParseServerMessage(message.Text!);
        if (!_helloDone && parsed is UnknownServerMessage { Type: "server/init" or "noise/handshake" or "server/error" } newer)
        {
            _logger.LogWarning("{Remote} answered with {Type}: it expects the newer encrypted Sendspin protocol", Remote, newer.Type);
            ProtocolMismatch?.Invoke(newer.Type);
            return false;
        }

        if (!_helloDone && parsed is not ServerHelloMessage)
        {
            _logger.LogDebug("Ignoring {Type} before server/hello", parsed.GetType().Name);
            return true;
        }

        switch (parsed)
        {
            case ServerHelloMessage hello:
                return await OnServerHelloAsync(hello.Hello, ct).ConfigureAwait(false);
            case ServerTimeMessage time:
                _clock.OnServerTime(time.Time, message.ReceivedAtMicros);
                break;
            case ServerStateMessage state:
                UpdateState(s =>
                {
                    if (state.Metadata is { } m)
                    {
                        s = s with { Metadata = MetadataState.Merge(s.Metadata, m) };
                    }

                    if (state.Controller is { } c)
                    {
                        s = s with { Controller = ControllerState.Merge(s.Controller, c) };
                    }

                    return s;
                });
                break;
            case GroupUpdateMessage group:
                UpdateState(s => s with { Group = GroupState.Merge(s.Group, group.Delta) });
                break;
            case ServerCommandMessage command:
                await OnServerCommandAsync(command.Command, ct).ConfigureAwait(false);
                break;
            case StreamStartMessage start when !_lifetime.IsCancellationRequested:
                OnStreamStart(start.Start);
                break;
            case StreamClearMessage clear when !_lifetime.IsCancellationRequested:
                if (clear.Clear.Roles is null || clear.Clear.Roles.Contains("player"))
                {
                    _pipeline.Clear();
                }

                break;
            case StreamEndMessage end when !_lifetime.IsCancellationRequested:
                OnStreamEnd(end.End);
                break;
        }

        return true;
    }

    private async Task<bool> OnServerHelloAsync(ServerHello hello, CancellationToken ct)
    {
        if (_helloDone)
        {
            return true;
        }

        _logger.LogInformation("Connected to {Name} ({Id}), reason {Reason}, roles {Roles}", hello.Name, hello.ServerId, hello.ConnectionReason, string.Join(',', hello.ActiveRoles));
        if (AcceptServerAsync is { } accept && !await accept(hello).ConfigureAwait(false))
        {
            _logger.LogInformation("Declining {Name}: another server is preferred", hello.Name);
            await DisconnectAsync(GoodbyeReasons.AnotherServer).ConfigureAwait(false);
            return false;
        }

        _helloDone = true;
        UpdateState(s => s with { ServerId = hello.ServerId, ServerName = hello.Name, ConnectionReason = hello.ConnectionReason });

        _clock.Reset();
        await SendStateAsync(ct).ConfigureAwait(false);
        _timeTask = _clock.RunAsync(text => _transport.SendTextAsync(text, ct), _options.TimeBurstInterval, ct);
        return true;
    }

    private async Task OnServerCommandAsync(ServerCommand command, CancellationToken ct)
    {
        if (command.Player is not { } p)
        {
            return;
        }

        switch (p.Command)
        {
            case "volume" when p.Volume is { } v:
                _volume.SetVolume(Math.Clamp(v, 0, 100));
                break;
            case "mute" when p.Mute is { } m:
                _volume.SetMuted(m);
                break;
            case "set_static_delay" when p.StaticDelayMs is { } d:
                _staticDelayMs = Math.Clamp(d, 0, 5000);
                _pipeline.StaticDelayMs = _staticDelayMs;
                StaticDelayChanged?.Invoke(_staticDelayMs);
                break;
            default:
                _logger.LogDebug("Ignoring player command {Command}", p.Command);
                return;
        }

        await SendStateAsync(ct).ConfigureAwait(false);
    }

    private void OnStreamStart(StreamStart start)
    {
        if (start.Player is { } format)
        {
            try
            {
                _pipeline.StartStream(format);
                UpdateState(s => s with { Stream = format });
                _logger.LogInformation("Stream started: {Codec} {Rate} Hz {Channels} ch {Bits} bit", format.Codec, format.SampleRate, format.Channels, format.BitDepth);
            }
            catch (Exception ex) when (ex is AudioDecodeException or FormatException)
            {
                _logger.LogError(ex, "Cannot play stream format {Codec}", format.Codec);
            }
            catch (Exception ex)
            {
                // e.g. no audio device available; keep the session alive so the user can fix the output.
                _logger.LogError(ex, "Could not start the audio output");
            }
        }
    }

    private void OnStreamEnd(StreamEnd end)
    {
        if (end.Roles is null || end.Roles.Contains("player"))
        {
            _pipeline.EndStream();
            UpdateState(s => s with { Stream = null });
        }

        if (end.Roles is null || end.Roles.Contains("artwork"))
        {
            UpdateState(s => s with { Artwork = null });
        }
    }

    private void HandleBinary(TransportMessage message)
    {
        if (!BinaryFrame.TryParse(message.Binary, out var frame))
        {
            return;
        }

        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (frame.Type == BinaryMessageTypes.Audio)
        {
            if (_state.StreamActive)
            {
                _pipeline.OnAudioChunk(frame.TimestampMicros, frame.Payload);
            }
        }
        else if (BinaryMessageTypes.IsArtwork(frame.Type) && frame.ArtworkChannel == 0)
        {
            _ = ApplyArtworkAsync(frame);
        }
    }

    private async Task ApplyArtworkAsync(BinaryFrame frame)
    {
        try
        {
            // Show the image when the server says so, but never hold it back for long.
            var delayMicros = _clock.IsSynchronized ? _clock.ServerToLocal(frame.TimestampMicros) - _localClock.NowMicros : 0;
            if (delayMicros > 1000)
            {
                await Task.Delay(TimeSpan.FromMicroseconds(Math.Min(delayMicros, 10_000_000)), _lifetime.Token).ConfigureAwait(false);
            }

            if (frame.Payload.IsEmpty)
            {
                UpdateState(s => s with { Artwork = null });
                return;
            }

            var data = frame.Payload.ToArray();
            var format = DetectImageFormat(data);
            var version = Interlocked.Increment(ref _artworkVersion);
            UpdateState(s => s with { Artwork = new ArtworkImage(data, format, version) });
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static string DetectImageFormat(byte[] data)
    {
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return "jpeg";
        }

        if (data.Length > 4 && data[0] == 0x89 && data[1] == (byte)'P')
        {
            return "png";
        }

        if (data.Length > 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return "bmp";
        }

        return "jpeg";
    }

    private void OnVolumeChanged()
    {
        if (!_helloDone)
        {
            return;
        }

        _ = SendStateSafeAsync();
    }

    private void OnPipelineSyncChanged(bool inSync)
    {
        if (!_helloDone)
        {
            return;
        }

        _syncDebounce?.Cancel();
        if (inSync)
        {
            _wasInSync = true;
            if (_reportedState != ClientStates.Synchronized)
            {
                _reportedState = ClientStates.Synchronized;
                _ = SendStateSafeAsync();
            }

            return;
        }

        if (!_wasInSync || !_state.StreamActive)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _syncDebounce = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.SyncErrorDebounce, cts.Token).ConfigureAwait(false);
                _reportedState = ClientStates.Error;
                await SendStateSafeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task SendStateSafeAsync()
    {
        try
        {
            await SendStateAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
        {
        }
    }

    private Task SendStateAsync(CancellationToken ct)
    {
        var payload = new PlayerStatePayload(
            _volume.Volume,
            _volume.Muted,
            _staticDelayMs,
            _options.RequiredLeadTimeMs,
            _options.MinBufferMs,
            ["set_static_delay"]);
        return _transport.SendTextAsync(MessageCodec.ClientState(new ClientState(_reportedState, payload)), ct);
    }

    private void UpdateState(Func<SessionState, SessionState> update)
    {
        SessionState snapshot;
        lock (_stateGate)
        {
            _state = update(_state);
            snapshot = _state;
        }

        StateChanged?.Invoke(snapshot);
    }
}
