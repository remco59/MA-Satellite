using System.Net.WebSockets;
using System.Text.Json;
using MaSatellite.Core.Security;
using MaSatellite.Core.Tests.Support;
using MaSatellite.Core.Time;
using MaSatellite.Core.Transport;

namespace MaSatellite.Core.Tests;

public class SecureTransportTests
{
    private static readonly ILocalClock Clock = StopwatchClock.Instance;

    private sealed class Rig : IAsyncDisposable
    {
        private readonly SendspinListener _listener;
        private readonly TaskCompletionSource<WebSocketTransport> _serverSide = new();

        public Rig(FakeSecureServer server, ISendspinKeyStore? keys = null, TimeSpan? handshakeTimeout = null)
        {
            Server = server;
            Keys = keys ?? new InMemoryKeyStore();
            // The *client* listens (server-initiated mode): the fake server connects to us.
            _listener = new SendspinListener(0, "/sendspin", async t =>
            {
                Transport = new SecureTransport((WebSocketTransport)t, Keys, MaSatellite.Core.Security.Noise.NoiseSuite.ChaChaPolySha256, handshakeTimeout: handshakeTimeout ?? TimeSpan.FromSeconds(5));
                _serverSide.TrySetResult((WebSocketTransport)t);
                await Done.Task;
            }, Clock);
            _listener.Start();
        }

        public int Port => _listener.Port;

        public FakeSecureServer Server { get; }

        public ISendspinKeyStore Keys { get; }

        public SecureTransport? Transport { get; private set; }

        public TaskCompletionSource Done { get; } = new();

        public Task ServerTask { get; private set; } = Task.CompletedTask;

        public Task<WebSocketTransport> WaitTransportAsync() => _serverSide.Task;

        public async Task ConnectServerAsync()
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_listener.Port}/sendspin"), CancellationToken.None);
            ServerTask = Server.RunAsync(new WebSocketTransport(socket, Clock, "fake-server"));
            await _serverSide.Task;
        }

        public async ValueTask DisposeAsync()
        {
            Done.TrySetResult();
            await _listener.DisposeAsync();
        }
    }

    private static async Task<(Rig Rig, SecureHandshakeResult Result)> HandshakenAsync(FakeSecureServer server, ISendspinKeyStore? keys = null)
    {
        var rig = new Rig(server, keys);
        await rig.ConnectServerAsync();
        var result = await rig.Transport!.HandshakeAsync(CancellationToken.None);
        return (rig, result);
    }

    private static async Task<TransportMessage> NextAsync(IAsyncEnumerator<TransportMessage> e, int ms = 5000)
    {
        using var cts = new CancellationTokenSource(ms);
        return await e.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(ms)) ? e.Current : throw new InvalidOperationException("stream ended");
    }

    private static async Task WaitUntilAsync(Func<bool> cond, string what)
    {
        var end = Environment.TickCount64 + 5000;
        while (!cond() && Environment.TickCount64 < end)
        {
            await Task.Delay(10);
        }

        Assert.True(cond(), what);
    }

    [Fact]
    public async Task Completes_the_handshake_with_the_sentinel_psk_and_exchanges_json()
    {
        var server = new FakeSecureServer(Clock);
        var (rig, result) = await HandshakenAsync(server);
        await using var _ = rig;

        Assert.Equal(server.ServerId, result.ServerId);
        Assert.Equal(PskCategory.Sentinel, result.PskCategory);
        Assert.False(result.CredentialMismatch);
        Assert.Equal(32, server.Identity.PublicKey.Length);
        await WaitUntilAsync(() => server.Handshaken, "server finished");
        Assert.Equal(rig.Keys.ClientId(), server.ClientId);
        Assert.Equal("25519_ChaChaPoly_SHA256", server.ClientInitSuite);
        Assert.Equal(server.HandshakeHash, result.HandshakeHash);

        await using var rx = rig.Transport!.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        await server.SendJsonAsync("server/hello", new { name = "Fake" });
        var hello = await NextAsync(rx);
        Assert.True(hello.IsText);
        Assert.Contains("server/hello", hello.Text);

        await rig.Transport.SendTextAsync("{\"type\":\"client/hello\",\"payload\":{\"name\":\"PC\"}}", CancellationToken.None);
        await WaitUntilAsync(() => server.Payloads("client/hello").Any(), "client hello received encrypted");
        Assert.Equal("PC", server.Payloads("client/hello").Single().GetProperty("name").GetString());
    }

    [Fact]
    public async Task Binary_messages_are_surfaced_with_their_id_byte()
    {
        var server = new FakeSecureServer(Clock);
        var (rig, _) = await HandshakenAsync(server);
        await using var _r = rig;
        await using var rx = rig.Transport!.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();

        await server.SendAudioAsync(123456, [9, 8, 7], sendAhead: 500);
        var m = await NextAsync(rx);

        Assert.False(m.IsText);
        var b = m.Binary.ToArray();
        Assert.Equal(4, b[0]);
        Assert.Equal(123456, System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(1, 8)));
        Assert.Equal(500u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(9, 4)));
        Assert.Equal([9, 8, 7], b[13..]);
    }

    [Fact]
    public async Task Large_messages_are_fragmented_and_reassembled_in_both_directions()
    {
        var server = new FakeSecureServer(Clock);
        var (rig, _) = await HandshakenAsync(server);
        await using var _r = rig;
        await using var rx = rig.Transport!.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();

        var big = new string('x', 200_000);
        await server.SendJsonAsync("server/state", new { blob = big });
        var m = await NextAsync(rx);
        Assert.True(m.IsText);
        Assert.Equal(200_000, JsonDocument.Parse(m.Text!).RootElement.GetProperty("payload").GetProperty("blob").GetString()!.Length);

        await rig.Transport.SendTextAsync(JsonSerializer.Serialize(new { type = "client/state", payload = new { blob = big } }), CancellationToken.None);
        await WaitUntilAsync(() => server.Payloads("client/state").Any(), "big client message arrived");
        Assert.Equal(200_000, server.Payloads("client/state").Single().GetProperty("blob").GetString()!.Length);

        // the stream still works afterwards
        await server.SendJsonAsync("server/command", new { player = new { command = "mute", mute = true } });
        Assert.Contains("server/command", (await NextAsync(rx)).Text);
    }

    [Fact]
    public async Task Pairing_psk_and_long_term_psk_are_selected_by_id()
    {
        var keys = new InMemoryKeyStore();
        var server = new FakeSecureServer(Clock) { FirstPsk = PskChoice.Pairing(keys.PairingPsk) };
        var (rig, result) = await HandshakenAsync(server, keys);
        await using (rig)
        {
            Assert.Equal(PskCategory.Pairing, result.PskCategory);
        }

        var lt = SendspinPsk.NewRandom();
        var server2 = new FakeSecureServer(Clock) { FirstPsk = PskChoice.LongTerm(lt) };
        keys.SaveRecord(new PairingRecord(server2.ServerId, lt));
        var (rig2, result2) = await HandshakenAsync(server2, keys);
        await using (rig2)
        {
            Assert.Equal(PskCategory.LongTerm, result2.PskCategory);
            Assert.False(result2.CredentialMismatch);
        }
    }

    [Fact]
    public async Task An_unknown_long_term_psk_falls_back_to_the_sentinel_and_flags_the_mismatch()
    {
        var server = new FakeSecureServer(Clock) { FirstPsk = PskChoice.LongTerm(SendspinPsk.NewRandom()) };
        var (rig, result) = await HandshakenAsync(server);
        await using var _ = rig;

        Assert.Equal(PskCategory.Sentinel, result.PskCategory);
        Assert.True(result.CredentialMismatch);
        await WaitUntilAsync(() => server.CredentialMismatchSeen, "server saw the authenticated mismatch signal");
    }

    [Fact]
    public async Task A_record_that_belongs_to_another_server_fails_the_handshake()
    {
        var keys = new InMemoryKeyStore();
        var lt = SendspinPsk.NewRandom();
        keys.SaveRecord(new PairingRecord("someone-else", lt));
        var server = new FakeSecureServer(Clock) { FirstPsk = PskChoice.LongTerm(lt) };
        await using var rig = new Rig(server, keys);
        await rig.ConnectServerAsync();

        await Assert.ThrowsAsync<SecureHandshakeException>(() => rig.Transport!.HandshakeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_tampered_prologue_fails_the_handshake()
    {
        var server = new FakeSecureServer(Clock) { TamperPrologue = true };
        await using var rig = new Rig(server);
        await rig.ConnectServerAsync();

        await Assert.ThrowsAsync<SecureHandshakeException>(() => rig.Transport!.HandshakeAsync(CancellationToken.None));
        Assert.NotNull(rig.Transport!.FailureReason);
    }

    [Fact]
    public async Task A_silent_server_times_out_quickly()
    {
        var socket = new ClientWebSocket();
        var server = new FakeSecureServer(Clock);
        await using var listenerRig = new Rig(server, handshakeTimeout: TimeSpan.FromMilliseconds(300));
        // Connect a server that never speaks the protocol.
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{listenerRig.Port}/sendspin"), CancellationToken.None);
        await listenerRig.WaitTransportAsync();

        var started = Environment.TickCount64;
        await Assert.ThrowsAsync<SecureHandshakeException>(() => listenerRig.Transport!.HandshakeAsync(CancellationToken.None));
        Assert.True(Environment.TickCount64 - started < 3000);
        socket.Dispose();
    }

    [Fact]
    public async Task Rehandshake_switches_keys_in_band_and_reports_the_new_psk()
    {
        var keys = new InMemoryKeyStore();
        var server = new FakeSecureServer(Clock);
        var (rig, _) = await HandshakenAsync(server, keys);
        await using var _r = rig;
        await using var rx = rig.Transport!.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        var rekeyed = 0;
        rig.Transport.Rekeyed += () => rekeyed++;
        var lt = SendspinPsk.NewRandom();
        keys.SaveRecord(new PairingRecord(server.ServerId, lt));
        await WaitUntilAsync(() => server.Handshaken, "handshaken");

        await server.RehandshakeAsync(PskChoice.LongTerm(lt));
        // the transport handles message 1 inside the receive loop; the next real message proves the new keys work
        var pending = rx.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => rekeyed == 1, "rekeyed");
        await server.SendJsonAsync("server/activate", new { activities = new[] { "playback" } });
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("server/activate", rx.Current.Text);

        Assert.Equal(PskCategory.LongTerm, rig.Transport.MatchedPskCategory);
        Assert.Equal(PskCategory.LongTerm, server.ActiveCategory);
        await rig.Transport.SendTextAsync("{\"type\":\"client/state\",\"payload\":{\"available\":true}}", CancellationToken.None);
        await WaitUntilAsync(() => server.Payloads("client/state").Any(), "message under new keys");
    }

    [Fact]
    public async Task Rehandshake_to_an_unknown_psk_closes_the_connection()
    {
        var server = new FakeSecureServer(Clock);
        var (rig, _) = await HandshakenAsync(server);
        await using var _r = rig;
        await using var rx = rig.Transport!.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        await WaitUntilAsync(() => server.Handshaken, "handshaken");

        var pending = rx.MoveNextAsync().AsTask();
        await server.RehandshakeAsync(PskChoice.LongTerm(SendspinPsk.NewRandom()));

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(rig.Transport.FailureReason);
    }

    [Fact]
    public async Task Tampered_ciphertext_and_cleartext_after_handshake_end_the_connection()
    {
        var server = new FakeSecureServer(Clock);
        var (rig, _) = await HandshakenAsync(server);
        await using var _r = rig;
        await using var rx = rig.Transport!.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        await WaitUntilAsync(() => server.Handshaken, "handshaken");

        var pending = rx.MoveNextAsync().AsTask();
        await server.SendTamperedAsync();
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("decryption", rig.Transport.FailureReason);

        var server2 = new FakeSecureServer(Clock);
        var (rig2, _) = await HandshakenAsync(server2);
        await using var _r2 = rig2;
        await using var rx2 = rig2.Transport!.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        await WaitUntilAsync(() => server2.Handshaken, "handshaken 2");
        var pending2 = rx2.MoveNextAsync().AsTask();
        await server2.SendCleartextAsync();
        Assert.False(await pending2.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("cleartext", rig2.Transport.FailureReason);
    }
}
