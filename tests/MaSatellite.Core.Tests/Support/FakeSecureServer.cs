using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaSatellite.Core.Security;
using MaSatellite.Core.Security.Noise;
using MaSatellite.Core.Time;
using MaSatellite.Core.Transport;

namespace MaSatellite.Core.Tests.Support;

internal sealed record PskChoice(byte[] Psk, string Category)
{
    public static PskChoice Sentinel { get; } = new(SendspinPsk.Sentinel, PskCategory.Sentinel);

    public static PskChoice Pairing(byte[] psk) => new(psk, PskCategory.Pairing);

    public static PskChoice LongTerm(byte[] psk) => new(psk, PskCategory.LongTerm);
}

/// <summary>A Sendspin server speaking the encrypted protocol: it is the Noise <b>initiator</b>, whichever side opened the WebSocket.</summary>
#pragma warning disable CA1001 // test helper; lives for one test
internal sealed class FakeSecureServer
{
    public const long ClockOffset = 7_000_000_000;

    private readonly ILocalClock _clock;
    private readonly List<(string Type, JsonElement Payload)> _received = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocketTransport? _ws;
    private NoiseCipherPair? _cipher;
    private NoiseKkPsk2Handshake? _pendingRehandshake;
    private PskChoice? _pendingChoice;
    private string? _activeCategory;
    private readonly List<byte> _fragments = [];
    private byte _fragmentType;

    public FakeSecureServer(ILocalClock clock, string name = "Fake Secure MA")
    {
        _clock = clock;
        Name = name;
    }

    public KeyPair Identity { get; } = KeyPair.Generate();

    public string ServerId => Base64Url.Encode(Identity.PublicKey);

    public string Name { get; }

    public NoiseSuite Suite { get; set; } = NoiseSuite.ChaChaPolySha256;

    public PskChoice FirstPsk { get; set; } = PskChoice.Sentinel;

    /// <summary>If the client answers with the Sentinel because it does not know <see cref="FirstPsk"/>, accept it (spec: Sentinel fallback).</summary>
    public bool AcceptSentinelFallback { get; set; } = true;

    /// <summary>Corrupts the prologue on the server side, as a tampering attacker would.</summary>
    public bool TamperPrologue { get; set; }

    public string? ClientId { get; private set; }

    public string? ClientInitSuite { get; private set; }

    public bool CredentialMismatchSeen { get; private set; }

    public bool Handshaken { get; private set; }

    public byte[]? HandshakeHash => _cipher?.HandshakeHash;

    public string? ActiveCategory => _activeCategory;

    public long ServerNow => _clock.NowMicros + ClockOffset;

    /// <summary>Called for every decrypted JSON message (after it was recorded). Runs on the receive loop.</summary>
    public Func<string, JsonElement, Task>? OnMessage { get; set; }

    public IReadOnlyList<(string Type, JsonElement Payload)> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    public IEnumerable<JsonElement> Payloads(string type) => Received.Where(r => r.Type == type).Select(r => r.Payload);

    /// <summary>Performs the handshake and then pumps messages until the socket closes.</summary>
    public async Task RunAsync(ISendspinTransport transport)
    {
        _ws = (WebSocketTransport)transport;
        await using var enumerator = _ws.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        if (!await HandshakeAsync(enumerator))
        {
            return;
        }

        Handshaken = true;
        while (await enumerator.MoveNextAsync())
        {
            var m = enumerator.Current;
            if (m.IsText)
            {
                return; // clients never send cleartext after the handshake
            }

            byte[] plaintext;
            try
            {
                plaintext = _cipher!.Decrypt(m.Binary.Span);
            }
            catch (CryptographicException)
            {
                return;
            }

            if (plaintext.Length == 0)
            {
                return;
            }

            if (plaintext[0] == 1)
            {
                var flags = plaintext[1];
                if ((flags & 2) != 0)
                {
                    _fragments.Clear();
                    _fragmentType = plaintext[2];
                    _fragments.AddRange(plaintext.AsSpan(3).ToArray());
                }
                else
                {
                    _fragments.AddRange(plaintext.AsSpan(2).ToArray());
                }

                if ((flags & 1) == 0)
                {
                    continue;
                }

                plaintext = [_fragmentType, .. _fragments];
            }

            if (plaintext[0] != 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(plaintext, 1, plaintext.Length - 1));
            var type = doc.RootElement.GetProperty("type").GetString()!;
            var payload = doc.RootElement.GetProperty("payload").Clone();
            lock (_received)
            {
                _received.Add((type, payload));
            }

            if (type == "noise/handshake" && _pendingRehandshake is not null)
            {
                _pendingRehandshake.ReadMessage2(Base64Url.Decode(payload.GetProperty("data").GetString()!));
                await _sendLock.WaitAsync();
                try
                {
                    _cipher = _pendingRehandshake.Split();
                    _activeCategory = _pendingChoice!.Category;
                }
                finally
                {
                    _sendLock.Release();
                }

                _pendingRehandshake = null;
                continue;
            }

            if (type == "client/time")
            {
                var received = ServerNow;
                await SendJsonAsync("server/time", new
                {
                    client_transmitted = payload.GetProperty("client_transmitted").GetInt64(),
                    server_received = received,
                    server_transmitted = received + 20,
                });
            }

            if (OnMessage is not null)
            {
                await OnMessage(type, payload);
            }
        }
    }

    public Task SendJsonAsync(string type, object payload) => SendPlaintextAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, payload })).Prepend((byte)0).ToArray());

    /// <summary>Sends a binary message whose first byte is the message id (e.g. 4 = audio).</summary>
    public Task SendBinaryAsync(byte[] idAndData) => SendPlaintextAsync(idAndData);

    public Task SendAudioAsync(long timestampMicros, byte[] encoded, uint sendAhead = 0)
    {
        var buf = new byte[1 + 8 + 4 + encoded.Length];
        buf[0] = 4;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), timestampMicros);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(9, 4), sendAhead);
        encoded.CopyTo(buf, 13);
        return SendPlaintextAsync(buf);
    }

    /// <summary>Starts an in-band re-handshake to another PSK; completes when the client's message 2 has been processed.</summary>
    public async Task RehandshakeAsync(PskChoice choice, string? clientPubKeyOverride = null)
    {
        var clientKey = Base64Url.Decode(clientPubKeyOverride ?? ClientId!);
        var hs = new NoiseKkPsk2Handshake(Suite, true, _cipher!.HandshakeHash, Identity, clientKey) { Psk = choice.Psk };
        var m1 = hs.WriteMessage1(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { psk_id = SendspinPsk.Id(choice.Psk), psk_category = choice.Category })));
        _pendingRehandshake = hs;
        _pendingChoice = choice;
        await SendJsonAsync("noise/handshake", new { data = Base64Url.Encode(m1) });
    }

    public Task CloseAsync() => _ws!.CloseAsync(CancellationToken.None);

    private async Task<bool> HandshakeAsync(IAsyncEnumerator<TransportMessage> enumerator)
    {
        if (!await enumerator.MoveNextAsync() || !enumerator.Current.IsText)
        {
            return false;
        }

        var clientInit = enumerator.Current.Text!;
        using (var doc = JsonDocument.Parse(clientInit))
        {
            var p = doc.RootElement.GetProperty("payload");
            ClientId = p.GetProperty("client_id").GetString();
            ClientInitSuite = p.GetProperty("suite").GetString();
        }

        if (!NoiseSuiteNames.TryParse(ClientInitSuite, out var suite))
        {
            await _ws!.SendTextAsync(JsonSerializer.Serialize(new { type = "server/error", payload = new { reason = "unsupported_suite" } }), CancellationToken.None);
            return false;
        }

        Suite = suite;
        var serverInit = JsonSerializer.Serialize(new { type = "server/init", payload = new { server_id = ServerId, version = 1 } });
        var prologue = Encoding.UTF8.GetBytes(clientInit + (TamperPrologue ? serverInit + " " : serverInit));
        var clientKey = Base64Url.Decode(ClientId!);
        var ephemeral = RandomNumberGenerator.GetBytes(32);

        NoiseKkPsk2Handshake Make(byte[] psk) => new(Suite, true, prologue, Identity, clientKey, ephemeral) { Psk = psk };
        var primary = Make(FirstPsk.Psk);
        var fallback = Make(SendspinPsk.Sentinel);
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { psk_id = SendspinPsk.Id(FirstPsk.Psk), psk_category = FirstPsk.Category }));
        var message1 = primary.WriteMessage1(payload);
        fallback.WriteMessage1(payload);

        await _ws!.SendTextAsync(serverInit, CancellationToken.None);
        await _ws.SendTextAsync(JsonSerializer.Serialize(new { type = "noise/handshake", payload = new { data = Base64Url.Encode(message1) } }), CancellationToken.None);

        if (!await enumerator.MoveNextAsync() || !enumerator.Current.IsText)
        {
            return false;
        }

        using var reply = JsonDocument.Parse(enumerator.Current.Text!);
        var message2 = Base64Url.Decode(reply.RootElement.GetProperty("payload").GetProperty("data").GetString()!);
        try
        {
            primary.ReadMessage2(message2);
            _cipher = primary.Split();
            _activeCategory = FirstPsk.Category;
            return true;
        }
        catch (CryptographicException) when (AcceptSentinelFallback && FirstPsk.Category != PskCategory.Sentinel)
        {
            try
            {
                fallback.ReadMessage2(message2);
            }
            catch (CryptographicException)
            {
                return false;
            }

            _cipher = fallback.Split();
            _activeCategory = PskCategory.Sentinel;
            CredentialMismatchSeen = true;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private async Task SendPlaintextAsync(byte[] plaintext)
    {
        await _sendLock.WaitAsync();
        try
        {
            const int max = 65535 - 16;
            if (plaintext.Length <= max)
            {
                await _ws!.SendBinaryAsync(_cipher!.Encrypt(plaintext), CancellationToken.None);
                return;
            }

            var type = plaintext[0];
            var offset = 1;
            var first = true;
            while (offset < plaintext.Length)
            {
                var overhead = first ? 3 : 2;
                var take = Math.Min(max - overhead, plaintext.Length - offset);
                var last = offset + take >= plaintext.Length;
                var frag = new byte[overhead + take];
                frag[0] = 1;
                frag[1] = (byte)((first ? 2 : 0) | (last ? 1 : 0));
                if (first)
                {
                    frag[2] = type;
                }

                Buffer.BlockCopy(plaintext, offset, frag, overhead, take);
                await _ws!.SendBinaryAsync(_cipher!.Encrypt(frag), CancellationToken.None);
                offset += take;
                first = false;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Test hook: sends a ciphertext that was altered after encryption.</summary>
    public async Task SendTamperedAsync()
    {
        var ct = _cipher!.Encrypt([0, (byte)'{', (byte)'}']);
        ct[0] ^= 0x80;
        await _ws!.SendBinaryAsync(ct, CancellationToken.None);
    }

    /// <summary>Test hook: sends a cleartext text message after the handshake (a protocol violation).</summary>
    public Task SendCleartextAsync() => _ws!.SendTextAsync("{\"type\":\"server/hello\",\"payload\":{}}", CancellationToken.None);
}
