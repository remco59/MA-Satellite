using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaSatellite.Core.Security;
using MaSatellite.Core.Security.Noise;
using MaSatellite.Core.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Core.Transport;

/// <summary>The handshake could not be completed; the connection is unusable. Per the spec most failures are silent (no message to the peer).</summary>
public sealed class SecureHandshakeException : Exception
{
    public SecureHandshakeException(string message)
        : base(message)
    {
    }

    public SecureHandshakeException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public sealed record SecureHandshakeResult(string ServerId, string PskCategory, bool CredentialMismatch, byte[] HandshakeHash);

/// <summary>
/// Encrypted Sendspin transport: cleartext <c>client/init</c> / <c>server/init</c> / <c>noise/handshake</c>, then every
/// WebSocket binary message is one Noise transport message whose plaintext is <c>[id][data]</c>. Id 0 is JSON (surfaced as text),
/// id 1 is fragmentation (reassembled here), anything else is surfaced as binary including the id byte.
/// In-band re-handshakes are handled here and reported through <see cref="Rekeyed"/>.
/// </summary>
public sealed class SecureTransport : ISendspinTransport
{
    /// <summary>Largest plaintext (id byte included) that fits in one Noise transport message.</summary>
    internal const int MaxPlaintext = 65535 - NoiseCrypto.TagLength;

    private const int MaxReassembledBytes = 16 * 1024 * 1024;
    private const byte FragmentId = 1;
    private const byte FlagLast = 1;
    private const byte FlagFirst = 2;

    private readonly WebSocketTransport _inner;
    private readonly ISendspinKeyStore _keys;
    private readonly NoiseSuite _suite;
    private readonly ILogger _logger;
    private readonly TimeSpan _handshakeTimeout;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private IAsyncEnumerator<TransportMessage>? _raw;
    private NoiseCipherPair? _cipher;
    private byte[]? _serverStatic;
    private bool _handshakeDone;
    private long _deadline;

    public SecureTransport(WebSocketTransport inner, ISendspinKeyStore keys, NoiseSuite suite, ILogger? logger = null, TimeSpan? handshakeTimeout = null)
    {
        _inner = inner;
        _keys = keys;
        _suite = suite;
        _logger = logger ?? NullLogger.Instance;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
    }

    public string RemoteDescription => _inner.RemoteDescription;

    public string? ServerId { get; private set; }

    /// <summary><see cref="PskCategory"/> of the PSK the current keys derive from.</summary>
    public string? MatchedPskCategory { get; private set; }

    /// <summary>True when the server referenced a credential we do not hold and we answered with the Sentinel PSK (authenticated mismatch signal).</summary>
    public bool CredentialMismatch { get; private set; }

    /// <summary>Why the connection was dropped, if the transport closed it because of a protocol or crypto failure.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Raised after an in-band re-handshake switched to new keys (the matched PSK may have changed).</summary>
    public event Action? Rekeyed;

    /// <summary>Runs the cleartext phase and the Noise handshake. Throws <see cref="SecureHandshakeException"/> on any failure.</summary>
    public async Task<SecureHandshakeResult> HandshakeAsync(CancellationToken cancellationToken)
    {
        _deadline = Environment.TickCount64 + (long)_handshakeTimeout.TotalMilliseconds;
        try
        {
            return await DoHandshakeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Fail("handshake timed out");
            throw new SecureHandshakeException("The server did not complete the encrypted handshake in time.");
        }
        catch (SecureHandshakeException ex)
        {
            Fail(ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Fail(ex.Message);
            throw new SecureHandshakeException("The encrypted handshake failed: " + ex.Message, ex);
        }
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var plaintext = new byte[body.Length + 1];
        body.CopyTo(plaintext, 1);
        await SendPlaintextAsync(plaintext, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TransportMessage> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_handshakeDone || _raw is null)
        {
            throw new InvalidOperationException("Call HandshakeAsync first.");
        }

        List<byte>? fragments = null;
        byte fragmentType = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            bool has;
            try
            {
                has = await _raw.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (!has)
            {
                yield break;
            }

            var message = _raw.Current;
            if (message.IsText)
            {
                Fail("cleartext message after the handshake");
                yield break;
            }

            byte[] plaintext;
            try
            {
                plaintext = _cipher!.Decrypt(message.Binary.Span);
            }
            catch (CryptographicException ex)
            {
                Fail("decryption failed: " + ex.Message);
                yield break;
            }

            if (plaintext.Length == 0)
            {
                Fail("empty message");
                yield break;
            }

            var id = plaintext[0];
            byte[] complete;
            if (id == FragmentId)
            {
                if (plaintext.Length < 2 || (plaintext[1] & 0xFC) != 0)
                {
                    Fail("malformed fragment");
                    yield break;
                }

                var flags = plaintext[1];
                var first = (flags & FlagFirst) != 0;
                var last = (flags & FlagLast) != 0;
                if (first)
                {
                    if (fragments is not null || plaintext.Length < 3 || plaintext[2] == FragmentId)
                    {
                        Fail("malformed fragment sequence");
                        yield break;
                    }

                    fragments = [];
                    fragmentType = plaintext[2];
                    fragments.AddRange(plaintext.AsSpan(3).ToArray());
                }
                else
                {
                    if (fragments is null)
                    {
                        Fail("unexpected fragment");
                        yield break;
                    }

                    fragments.AddRange(plaintext.AsSpan(2).ToArray());
                }

                if (fragments.Count > MaxReassembledBytes)
                {
                    Fail("reassembled message too large");
                    yield break;
                }

                if (!last)
                {
                    continue;
                }

                complete = new byte[fragments.Count + 1];
                complete[0] = fragmentType;
                fragments.CopyTo(complete, 1);
                fragments = null;
            }
            else
            {
                if (fragments is not null)
                {
                    Fail("message inside a fragmented transfer");
                    yield break;
                }

                complete = plaintext;
            }

            if (complete[0] == 0)
            {
                var json = Encoding.UTF8.GetString(complete, 1, complete.Length - 1);
                if (IsNoiseHandshake(json, out var data))
                {
                    var ok = await RehandshakeAsync(data, cancellationToken).ConfigureAwait(false);
                    if (!ok)
                    {
                        yield break;
                    }

                    continue;
                }

                yield return TransportMessage.FromText(json, message.ReceivedAtMicros);
            }
            else
            {
                yield return TransportMessage.FromBinary(complete, message.ReceivedAtMicros);
            }
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken) => _inner.CloseAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_raw is not null)
        {
            try
            {
                await _raw.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        await _inner.DisposeAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private async Task<SecureHandshakeResult> DoHandshakeAsync(CancellationToken ct)
    {
        var clientId = _keys.ClientId();
        var init = JsonSerializer.Serialize(new { type = "client/init", payload = new { client_id = clientId, version = 1, suite = _suite.SuiteName() } });
        _raw = _inner.ReceiveAsync(ct).GetAsyncEnumerator(ct);
        await _inner.SendTextAsync(init, ct).ConfigureAwait(false);

        var serverInit = await NextRawTextAsync(ct).ConfigureAwait(false);
        using (var doc = JsonDocument.Parse(serverInit))
        {
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            var payload = root.GetProperty("payload");
            if (type == "server/error")
            {
                throw new SecureHandshakeException("The server refused the connection: " + (payload.TryGetProperty("reason", out var r) ? r.GetString() : "unknown"));
            }

            if (type != "server/init" || !payload.TryGetProperty("version", out var v) || v.GetInt32() != 1)
            {
                throw new SecureHandshakeException("Unexpected reply to client/init.");
            }

            if (!Base64Url.TryDecode(payload.GetProperty("server_id").GetString(), 32, out var serverKey))
            {
                throw new SecureHandshakeException("The server sent an invalid server_id.");
            }

            _serverStatic = serverKey;
            ServerId = Base64Url.Encode(serverKey);
        }

        var handshakeText = await NextRawTextAsync(ct).ConfigureAwait(false);
        if (!IsNoiseHandshake(handshakeText, out var data))
        {
            throw new SecureHandshakeException("Expected noise/handshake after server/init.");
        }

        var prologue = Encoding.UTF8.GetBytes(init + serverInit);
        var handshake = new NoiseKkPsk2Handshake(_suite, initiator: false, prologue, _keys.Identity, _serverStatic);
        var payloadBytes = handshake.ReadMessage1(Base64Url.Decode(data));
        var selection = SelectPsk(payloadBytes, ServerId!, allowSentinelFallback: true);
        handshake.Psk = selection.Psk;

        var message2 = handshake.WriteMessage2("{}"u8);
        var wire = JsonSerializer.Serialize(new { type = "noise/handshake", payload = new { data = Base64Url.Encode(message2) } });
        await _inner.SendTextAsync(wire, ct).ConfigureAwait(false);

        _cipher = handshake.Split();
        MatchedPskCategory = selection.Category;
        CredentialMismatch = selection.Mismatch;
        _handshakeDone = true;
        return new SecureHandshakeResult(ServerId!, selection.Category, selection.Mismatch, _cipher.HandshakeHash);
    }

    private async Task<bool> RehandshakeAsync(string data, CancellationToken ct)
    {
        try
        {
            var handshake = new NoiseKkPsk2Handshake(_suite, initiator: false, _cipher!.HandshakeHash, _keys.Identity, _serverStatic!);
            var payload = handshake.ReadMessage1(Base64Url.Decode(data));
            var selection = SelectPsk(payload, ServerId!, allowSentinelFallback: false);
            handshake.Psk = selection.Psk;
            var message2 = handshake.WriteMessage2("{}"u8);
            var wire = JsonSerializer.Serialize(new { type = "noise/handshake", payload = new { data = Base64Url.Encode(message2) } });
            var body = Encoding.UTF8.GetBytes(wire);
            var plaintext = new byte[body.Length + 1];
            body.CopyTo(plaintext, 1);

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Message 2 still travels under the old keys; everything after it uses the new ones.
                await SendFramedAsync(plaintext, ct).ConfigureAwait(false);
                _cipher = handshake.Split();
                MatchedPskCategory = selection.Category;
                CredentialMismatch = false;
            }
            finally
            {
                _sendLock.Release();
            }

            Rekeyed?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or SecureHandshakeException or JsonException or InvalidOperationException)
        {
            Fail("re-handshake failed: " + ex.Message);
            return false;
        }
    }

    private (byte[] Psk, string Category, bool Mismatch) SelectPsk(byte[] message1Payload, string serverId, bool allowSentinelFallback)
    {
        using var doc = JsonDocument.Parse(message1Payload);
        var pskId = doc.RootElement.GetProperty("psk_id").GetString() ?? string.Empty;
        var category = doc.RootElement.GetProperty("psk_category").GetString();
        if (!PskCategory.IsKnown(category))
        {
            throw new SecureHandshakeException("Unknown psk_category.");
        }

        switch (category)
        {
            case PskCategory.LongTerm:
                if (_keys.FindRecordByPskId(pskId) is { } record)
                {
                    if (record.ServerId != serverId)
                    {
                        throw new SecureHandshakeException("The pairing record belongs to a different server.");
                    }

                    return (record.LongTermPsk, PskCategory.LongTerm, false);
                }

                break;
            case PskCategory.Pairing:
                if (SendspinPsk.Id(_keys.PairingPsk) == pskId)
                {
                    return (_keys.PairingPsk, PskCategory.Pairing, false);
                }

                break;
            case PskCategory.Sentinel:
                if (pskId == SendspinPsk.SentinelId)
                {
                    return (SendspinPsk.Sentinel, PskCategory.Sentinel, false);
                }

                break;
        }

        if (!allowSentinelFallback)
        {
            throw new SecureHandshakeException("The server referenced a PSK we do not hold.");
        }

        _logger.LogInformation("Server referenced an unknown {Category} PSK; answering with the Sentinel PSK", category);
        return (SendspinPsk.Sentinel, PskCategory.Sentinel, true);
    }

    private async Task<string> NextRawTextAsync(CancellationToken ct)
    {
        var left = TimeSpan.FromMilliseconds(Math.Max(_deadline - Environment.TickCount64, 1));
        bool has;
        try
        {
            has = await _raw!.MoveNextAsync().AsTask().WaitAsync(left, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new SecureHandshakeException("The handshake was cancelled.");
        }

        if (!has)
        {
            throw new SecureHandshakeException("The server closed the connection during the handshake.");
        }

        var m = _raw.Current;
        return m.IsText ? m.Text! : throw new SecureHandshakeException("Expected a text message during the handshake.");
    }

    private static bool IsNoiseHandshake(string json, out string data)
    {
        data = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "noise/handshake"
                && doc.RootElement.TryGetProperty("payload", out var p) && p.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
            {
                data = d.GetString()!;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private async Task SendPlaintextAsync(byte[] plaintext, CancellationToken ct)
    {
        if (!_handshakeDone)
        {
            throw new InvalidOperationException("The handshake has not completed.");
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendFramedAsync(plaintext, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Encrypts and sends, fragmenting when needed. The caller holds the send lock so nonces follow send order.</summary>
    private async Task SendFramedAsync(byte[] plaintext, CancellationToken ct)
    {
        if (plaintext.Length <= MaxPlaintext)
        {
            await _inner.SendBinaryAsync(_cipher!.Encrypt(plaintext), ct).ConfigureAwait(false);
            return;
        }

        var type = plaintext[0];
        var offset = 1;
        var first = true;
        while (offset < plaintext.Length)
        {
            var overhead = first ? 3 : 2;
            var take = Math.Min(MaxPlaintext - overhead, plaintext.Length - offset);
            var last = offset + take >= plaintext.Length;
            var fragment = new byte[overhead + take];
            fragment[0] = FragmentId;
            fragment[1] = (byte)((first ? FlagFirst : 0) | (last ? FlagLast : 0));
            if (first)
            {
                fragment[2] = type;
            }

            Buffer.BlockCopy(plaintext, offset, fragment, overhead, take);
            await _inner.SendBinaryAsync(_cipher!.Encrypt(fragment), ct).ConfigureAwait(false);
            offset += take;
            first = false;
        }
    }

    private void Fail(string reason)
    {
        FailureReason ??= reason;
        _logger.LogWarning("Closing {Remote}: {Reason}", RemoteDescription, reason);
        _ = _inner.CloseAsync(CancellationToken.None);
    }
}
