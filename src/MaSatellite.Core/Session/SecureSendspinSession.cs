using System.Buffers.Binary;
using System.Text;
using MaSatellite.Core.Audio;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Security;
using MaSatellite.Core.Time;
using MaSatellite.Core.Transport;
using MaSatellite.Core.Volume;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Core.Session;

/// <summary>One connection speaking the encrypted Sendspin protocol: activation, availability, unpaired access and Pairing PSK.</summary>
public sealed class SecureSendspinSession : ISendspinSession
{
    private static readonly string[] OfferedPairMethods = ["pairing_psk"];

    private readonly SecureTransport _transport;
    private readonly SendspinOptions _options;
    private readonly ClockSync _clock;
    private readonly PlayerPipeline _pipeline;
    private readonly IVolumeControl _volume;
    private readonly ILocalClock _localClock;
    private readonly ISendspinKeyStore _keys;
    private readonly ILogger _logger;
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _activeRoles = [];
    private readonly ArtworkAssembler _artwork = new();

    private SessionState _state = new();
    private HashSet<string> _activities = [];
    private int _outputDelayMs;
    private bool _firstActivation = true;
    private bool _helloReceived;
    private bool _available;
    private bool _stopped;
    private string _serverName = string.Empty;
    private int _pairingIndex;
    private byte[]? _pendingLongTermPsk;
    private int _artworkVersion;
    private CancellationTokenSource? _metadataSchedule;
    private CancellationTokenSource? _artworkSchedule;
    private Task? _timeTask;
    private Task? _availabilityTask;
    private bool _disposed;

    public SecureSendspinSession(
        SecureTransport transport,
        SendspinOptions options,
        ClockSync clock,
        PlayerPipeline pipeline,
        IVolumeControl volume,
        ILocalClock localClock,
        ISendspinKeyStore keys,
        ILogger? logger = null)
    {
        _transport = transport;
        _options = options;
        _clock = clock;
        _pipeline = pipeline;
        _volume = volume;
        _localClock = localClock;
        _keys = keys;
        _logger = logger ?? NullLogger.Instance;
        _outputDelayMs = options.StaticDelayMs;
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

    public Func<ServerAdmission, Task<bool>>? AcceptServerAsync { get; set; }

    public event Action<SessionState>? StateChanged;

    public event Action<int>? StaticDelayChanged;

    public event Action<string>? ProtocolMismatch;

    /// <summary>Raised when a pairing finished and the record was saved.</summary>
    public event Action<string>? PairingCompleted;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var ct = linked.Token;
        _pipeline.StaticDelayMs = _outputDelayMs;
        _volume.Changed += OnVolumeChanged;
        try
        {
            SecureHandshakeResult handshake;
            try
            {
                handshake = await _transport.HandshakeAsync(ct).ConfigureAwait(false);
            }
            catch (SecureHandshakeException ex)
            {
                _logger.LogWarning("Encrypted handshake with {Remote} failed: {Reason}", Remote, ex.Message);
                ProtocolMismatch?.Invoke(ex.Message);
                return;
            }

            _transport.Rekeyed += OnRekeyed;
            UpdateSecurity();
            _logger.LogInformation("Encrypted session with {Remote} (server {Id}, {Category}{Mismatch})", Remote, handshake.ServerId, handshake.PskCategory, handshake.CredentialMismatch ? ", credential mismatch" : string.Empty);
            if (handshake.CredentialMismatch)
            {
                _logger.LogWarning("The server's pairing record for this player is gone; it should offer to pair again.");
            }

            _ = ActivationWatchdogAsync(linked);

            await foreach (var message in _transport.ReceiveAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var proceed = message.IsText
                        ? await HandleTextAsync(message, ct).ConfigureAwait(false)
                        : HandleBinary(message);
                    if (!proceed)
                    {
                        break;
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
            _volume.Changed -= OnVolumeChanged;
            _transport.Rekeyed -= OnRekeyed;
            await linked.CancelAsync().ConfigureAwait(false);
            foreach (var t in new[] { _timeTask, _availabilityTask })
            {
                if (t is null)
                {
                    continue;
                }

                try
                {
                    await t.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                }
            }

            _metadataSchedule?.Cancel();
            _artworkSchedule?.Cancel();
            _logger.LogInformation("Encrypted session with {Remote} ended{Reason}", Remote, _transport.FailureReason is { } r ? $" ({r})" : string.Empty);
        }
    }

    public async Task SendCommandAsync(ControllerCommand command, CancellationToken cancellationToken = default)
    {
        if (!_activeRoles.Contains(Roles.Controller))
        {
            return;
        }

        var controller = State.Controller;
        if (!controller.Supports(command.Command))
        {
            _logger.LogDebug("Server does not support command {Command}", command.Command);
            return;
        }

        await _transport.SendTextAsync(MessageCodec.ClientCommand(command), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetStaticDelayAsync(int delayMs, CancellationToken cancellationToken = default)
    {
        _outputDelayMs = Math.Clamp(delayMs, 0, 5000);
        _pipeline.StaticDelayMs = _outputDelayMs;
        if (_available)
        {
            await SendStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

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
            _stopped = true;
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
        await _transport.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task ActivationWatchdogAsync(CancellationTokenSource linked)
    {
        try
        {
            await Task.Delay(_options.ActivationTimeout, linked.Token).ConfigureAwait(false);
            if (_firstActivation)
            {
                _logger.LogWarning("No server/activate from {Remote} within {Timeout}", Remote, _options.ActivationTimeout);
                await linked.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> HandleTextAsync(TransportMessage message, CancellationToken ct)
    {
        var parsed = MessageCodec.ParseServerMessage(message.Text!);
        if (!_helloReceived && parsed is not ServerHelloMessage)
        {
            _logger.LogDebug("Ignoring {Type} before server/hello", parsed.GetType().Name);
            return true;
        }

        if (_firstActivation && parsed is not (ServerHelloMessage or ServerActivateMessage))
        {
            return true; // the server must activate us first; nothing else is valid yet
        }

        switch (parsed)
        {
            case ServerHelloMessage hello:
                _helloReceived = true;
                _serverName = hello.Hello.Name;
                UpdateState(s => s with { ServerId = _transport.ServerId, ServerName = _serverName });
                await _transport.SendTextAsync(MessageCodecV2.ClientHello(BuildHello()), ct).ConfigureAwait(false);
                break;
            case ServerActivateMessage activate:
                return await OnActivateAsync(activate, ct).ConfigureAwait(false);
            case ServerTimeMessage time:
                _clock.OnServerTime(time.Time, message.ReceivedAtMicros);
                break;
            case ServerStateMessage state:
                OnServerState(state);
                break;
            case GroupUpdateMessage group:
                UpdateState(s => s with { Group = GroupState.Merge(new GroupState(), group.Delta) });
                break;
            case ServerCommandMessage command:
                await OnServerCommandAsync(command.Command, ct).ConfigureAwait(false);
                break;
            case StreamStartMessage start:
                OnStreamStart(start.Start);
                break;
            case StreamClearMessage clear:
                if (_activeRoles.Contains(Roles.Player) && (clear.Clear.Roles is null || clear.Clear.Roles.Contains("player")))
                {
                    _pipeline.Clear();
                }

                break;
            case StreamEndMessage end:
                OnStreamEnd(end.End);
                break;
            case ServerUnpairMessage:
                return await OnUnpairAsync().ConfigureAwait(false);
            case ServerPairFinalizeMessage:
                OnPairFinalized();
                break;
            case PairAbortMessage abort:
                _logger.LogInformation("The server aborted pairing: {Reason}", abort.Reason);
                _pendingLongTermPsk = null;
                UpdateState(s => s with { Pairing = false });
                break;
        }

        return true;
    }

    private ClientHelloV2 BuildHello()
    {
        var roles = new List<string> { Roles.Player, Roles.Controller, Roles.Metadata };
        if (_options.RequestArtwork)
        {
            roles.Add(Roles.Artwork);
        }

        return new ClientHelloV2(
            MessageCodecV2.TruncateUtf8(_options.PlayerName, 64),
            new DeviceInfo("ma-satellite", "ma-satellite", MessageCodecV2.TruncateUtf8(_options.SoftwareVersion, 64)),
            roles,
            new PlayerSupport(_options.SupportedFormats, _options.BufferCapacityBytes, []),
            new Dictionary<string, PairMethodDescriptor> { ["pairing_psk"] = new(["device"]) },
            new UnpairedAccess(_options.AllowUnpairedAccess));
    }

    private async Task<bool> OnActivateAsync(ServerActivateMessage msg, CancellationToken ct)
    {
        var category = _transport.MatchedPskCategory ?? PskCategory.Sentinel;
        var activities = msg.Activities.ToHashSet();
        var roles = msg.ActiveRoles is { } given ? given.ToHashSet() : _firstActivation ? [] : _activeRoles.ToHashSet();

        var verdict = ActivationValidator.Validate(category, _options.AllowUnpairedAccess, activities, roles, msg.PairingMethod, msg.PairingFormat, OfferedPairMethods);
        switch (verdict)
        {
            case ActivationVerdict.PairingRequired:
                _logger.LogWarning("The server wants to use this player without pairing, which is disabled in the settings");
                await DisconnectAsync(GoodbyeReasonsV2.PairingRequired).ConfigureAwait(false);
                return false;
            case ActivationVerdict.Unauthorized:
                _logger.LogWarning("The server declared an activation this connection is not entitled to");
                await DisconnectAsync(GoodbyeReasonsV2.Unauthorized).ConfigureAwait(false);
                return false;
            case ActivationVerdict.MethodNotSupported:
                await _transport.SendTextAsync(MessageCodecV2.PairAbort(PairAbortReasons.MethodNotSupported), ct).ConfigureAwait(false);
                return true;
        }

        if (_firstActivation)
        {
            var rank = activities.Contains(Activities.Playback) ? AdmissionRank.Playback : activities.Contains(Activities.Pairing) ? AdmissionRank.Pairing : AdmissionRank.None;
            if (AcceptServerAsync is { } accept && !await accept(new ServerAdmission(_transport.ServerId ?? string.Empty, _serverName, rank)).ConfigureAwait(false))
            {
                _logger.LogInformation("Declining {Name}: another server holds this player", _serverName);
                await DisconnectAsync(GoodbyeReasonsV2.ConcurrentAttempt).ConfigureAwait(false);
                return false;
            }
        }

        // An activation that arrives while a pairing delivery is in flight cancels it (nothing is persisted).
        _pendingLongTermPsk = null;

        var removed = _activeRoles.Except(roles).ToList();
        var added = roles.Except(_activeRoles).ToList();
        foreach (var role in removed)
        {
            RemoveRole(role);
        }

        _activeRoles.Clear();
        foreach (var r in roles)
        {
            _activeRoles.Add(r);
        }

        _activities = activities;
        var unpaired = category != PskCategory.LongTerm;
        UpdateState(s => s with
        {
            ActiveRoles = [.. roles],
            Activities = [.. activities],
            Pairing = activities.Contains(Activities.Pairing),
            AwaitingApproval = unpaired && roles.Count == 0 && !activities.Contains(Activities.Pairing),
        });

        var first = _firstActivation;
        _firstActivation = false;
        if (first)
        {
            _clock.Reset();
            _timeTask = _clock.RunAsync(text => _transport.SendTextAsync(text, ct), _options.TimeBurstInterval, ct);
            _availabilityTask = ReportAvailabilityWhenSyncedAsync(ct);
        }
        else if (_available && (added.Count > 0 || removed.Count > 0))
        {
            await SendStateAsync(ct).ConfigureAwait(false);
        }

        if (activities.Contains(Activities.Pairing))
        {
            await StartPairingAsync(ct).ConfigureAwait(false);
        }

        return true;
    }

    private void RemoveRole(string role)
    {
        switch (role)
        {
            case Roles.Player:
                _pipeline.EndStream();
                UpdateState(s => s with { Stream = null });
                break;
            case Roles.Metadata:
                _metadataSchedule?.Cancel();
                UpdateState(s => s with { Metadata = new MetadataState() });
                break;
            case Roles.Controller:
                UpdateState(s => s with { Controller = new ControllerState() });
                break;
            case Roles.Artwork:
                _artworkSchedule?.Cancel();
                _artwork.Reset();
                UpdateState(s => s with { Artwork = null });
                break;
        }
    }

    private async Task ReportAvailabilityWhenSyncedAsync(CancellationToken ct)
    {
        try
        {
            while (!_clock.IsSynchronized)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            _available = true;
            await SendStateAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task SendStateAsync(CancellationToken ct)
    {
        PlayerStateV2? player = null;
        if (_activeRoles.Contains(Roles.Player))
        {
            player = new PlayerStateV2(
                _volume.Volume,
                _volume.Muted,
                _outputDelayMs,
                _options.RequiredLeadTimeMs,
                _options.MinBufferMs,
                ["volume", "mute", "set_output_delay"]);
        }

        ArtworkStateV2? artwork = null;
        if (_activeRoles.Contains(Roles.Artwork))
        {
            artwork = new ArtworkStateV2([new ArtworkChannelStateV2("album", "jpeg", _options.ArtworkSize, _options.ArtworkSize)]);
        }

        return _transport.SendTextAsync(MessageCodecV2.ClientState(new ClientStateV2(_available, player, artwork)), ct);
    }

    private void OnServerState(ServerStateMessage state)
    {
        if (state.Controller is { } c && _activeRoles.Contains(Roles.Controller))
        {
            UpdateState(s => s with { Controller = ControllerState.Merge(new ControllerState(), c) });
        }

        if (state.Metadata is { } m && _activeRoles.Contains(Roles.Metadata))
        {
            ApplyOrScheduleMetadata(MetadataState.Merge(new MetadataState(), m));
        }
    }

    /// <summary>Metadata whose timestamp is still in the future becomes the single pending update and takes effect at that moment.</summary>
    private void ApplyOrScheduleMetadata(MetadataState metadata)
    {
        _metadataSchedule?.Cancel();
        var delay = DelayUntil(metadata.Timestamp);
        if (delay <= TimeSpan.Zero)
        {
            UpdateState(s => s with { Metadata = metadata });
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _metadataSchedule = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                UpdateState(s => s with { Metadata = metadata });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private TimeSpan DelayUntil(long serverTimestampMicros)
    {
        if (!_clock.IsSynchronized || serverTimestampMicros == 0)
        {
            return TimeSpan.Zero;
        }

        var micros = _clock.ServerToLocal(serverTimestampMicros) - _localClock.NowMicros;
        return micros <= 1000 ? TimeSpan.Zero : TimeSpan.FromMicroseconds(Math.Min(micros, 30_000_000));
    }

    private async Task OnServerCommandAsync(ServerCommand command, CancellationToken ct)
    {
        if (command.Player is not { } p || !_activeRoles.Contains(Roles.Player))
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
            case "set_output_delay" when p.StaticDelayMs is { } d:
                _outputDelayMs = Math.Clamp(d, 0, 5000);
                _pipeline.StaticDelayMs = _outputDelayMs;
                StaticDelayChanged?.Invoke(_outputDelayMs);
                break;
            default:
                _logger.LogDebug("Ignoring player command {Command}", p.Command);
                return;
        }

        if (_available)
        {
            await SendStateAsync(ct).ConfigureAwait(false);
        }
    }

    private void OnStreamStart(StreamStart start)
    {
        if (start.Player is { } format && _activeRoles.Contains(Roles.Player))
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
                _logger.LogError(ex, "Could not start the audio output");
            }
        }
    }

    private void OnStreamEnd(StreamEnd end)
    {
        if (_activeRoles.Contains(Roles.Player) && (end.Roles is null || end.Roles.Contains("player")))
        {
            _pipeline.EndStream();
            UpdateState(s => s with { Stream = null });
        }

        if (end.Roles is null || end.Roles.Contains("artwork"))
        {
            _artworkSchedule?.Cancel();
            _artwork.Reset();
            UpdateState(s => s with { Artwork = null });
        }
    }

    private bool HandleBinary(TransportMessage message)
    {
        if (_lifetime.IsCancellationRequested || _firstActivation)
        {
            return true;
        }

        var data = message.Binary;
        var span = data.Span;
        if (span.Length == 0)
        {
            return false;
        }

        var id = span[0];
        if (id == BinaryMessageTypes.Audio)
        {
            if (span.Length < 13)
            {
                _logger.LogWarning("Audio message shorter than its 13-byte header; closing");
                return false; // protocol error
            }

            if (_activeRoles.Contains(Roles.Player) && _state.StreamActive)
            {
                var timestamp = BinaryPrimitives.ReadInt64BigEndian(span.Slice(1, 8));
                _pipeline.OnAudioChunk(timestamp, data[13..]);
            }

            return true;
        }

        if (BinaryMessageTypes.IsArtwork(id))
        {
            return HandleArtwork(span, id - BinaryMessageTypes.ArtworkChannel0);
        }

        return true; // unknown binary ids are ignored
    }

    private bool HandleArtwork(ReadOnlySpan<byte> span, int channel)
    {
        if (!_activeRoles.Contains(Roles.Artwork))
        {
            return true;
        }

        switch (_artwork.Process(channel, span, out var completed))
        {
            case ArtworkResult.ProtocolError:
                _logger.LogWarning("Malformed artwork message; closing");
                return false;
            case ArtworkResult.Cancelled:
                _artworkSchedule?.Cancel();
                break;
            case ArtworkResult.Completed when channel == 0 && completed is { } image:
                ScheduleArtwork(image);
                break;
        }

        return true;
    }

    private void ScheduleArtwork(ArtworkImageComplete image)
    {
        _artworkSchedule?.Cancel();
        var delay = DelayUntil(image.TimestampMicros);
        void Apply()
        {
            if (image.Data.Length == 0)
            {
                UpdateState(s => s with { Artwork = null });
                return;
            }

            var version = Interlocked.Increment(ref _artworkVersion);
            UpdateState(s => s with { Artwork = new ArtworkImage(image.Data, SendspinSession.DetectImageFormat(image.Data), version) });
        }

        if (delay <= TimeSpan.Zero)
        {
            Apply();
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _artworkSchedule = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                Apply();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task StartPairingAsync(CancellationToken ct)
    {
        _pairingIndex++;
        var psk = SendspinPsk.NewRandom();
        while (psk.AsSpan().SequenceEqual(SendspinPsk.Sentinel) || psk.AsSpan().SequenceEqual(_keys.PairingPsk))
        {
            psk = SendspinPsk.NewRandom();
        }

        _pendingLongTermPsk = psk;
        _logger.LogInformation("Pairing with {Name} started", _serverName);
        await _transport.SendTextAsync(MessageCodecV2.PairInit(_pairingIndex), ct).ConfigureAwait(false);
        await _transport.SendTextAsync(MessageCodecV2.PairFinalize(Base64Url.Encode(psk)), ct).ConfigureAwait(false);
    }

    private void OnPairFinalized()
    {
        if (_pendingLongTermPsk is not { } psk || _transport.ServerId is not { } serverId)
        {
            return;
        }

        _keys.SaveRecord(new PairingRecord(serverId, psk));
        _pendingLongTermPsk = null;
        _logger.LogInformation("Paired with {Name}", _serverName);
        UpdateState(s => s with { Pairing = false });
        PairingCompleted?.Invoke(serverId);
    }

    private async Task<bool> OnUnpairAsync()
    {
        if (_transport.MatchedPskCategory != PskCategory.LongTerm || _transport.ServerId is not { } serverId)
        {
            return true; // unpaired sessions ignore server/unpair
        }

        _keys.RemoveRecord(serverId);
        _logger.LogInformation("{Name} removed its pairing with this player", _serverName);
        await DisconnectAsync(GoodbyeReasonsV2.Unpaired).ConfigureAwait(false);
        return false;
    }

    private void OnRekeyed()
    {
        _pairingIndex = 0; // counts pairing activations since the last Noise handshake
        UpdateSecurity();
    }

    private void UpdateSecurity()
    {
        var security = _transport.MatchedPskCategory == PskCategory.LongTerm ? SessionSecurity.EncryptedPaired : SessionSecurity.EncryptedUnpaired;
        UpdateState(s => s with { Security = security, ServerId = _transport.ServerId });
    }

    private void OnVolumeChanged()
    {
        if (!_available || _stopped)
        {
            return;
        }

        _ = SendStateSafeAsync();
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

    internal static string Describe(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
