using System.Net.WebSockets;
using System.Text.Json;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Security;
using MaSatellite.Core.Session;
using MaSatellite.Core.Tests.Support;
using MaSatellite.Core.Time;
using MaSatellite.Core.Transport;
using MaSatellite.Core.Volume;

namespace MaSatellite.Core.Tests;

public class SecureSendspinServiceTests
{
    private static readonly ILocalClock Clock = StopwatchClock.Instance;
    private static readonly string[] PlaybackRoles = ["player@v1", "controller@v1", "metadata@v1", "artwork@v1"];

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(what ?? "condition not met");
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(SendspinOptions? options = null)
        {
            Output = new FakeAudioOutput(Clock);
            Volume = new InMemoryVolumeControl();
            Keys = new InMemoryKeyStore();
            Service = new SendspinService(Output, Volume, Clock, keyStore: Keys);
            Options = options ?? new SendspinOptions { PlayerName = "Test PC", ProtocolMode = SendspinProtocolMode.Encrypted };
        }

        public FakeAudioOutput Output { get; }

        public InMemoryVolumeControl Volume { get; }

        public InMemoryKeyStore Keys { get; }

        public SendspinService Service { get; }

        public SendspinOptions Options { get; }

        public async Task StartAsync() => await Service.StartAsync(Options, new ServerConnectionOptions { ListenPort = 0 });

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Output.Dispose();
        }
    }

    private static async Task<FakeSecureServer> ConnectAsync(Rig rig, Action<FakeSecureServer>? configure = null, string name = "Secure MA")
    {
        var fake = new FakeSecureServer(Clock, name);
        configure?.Invoke(fake);
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Service.ListenPort}/sendspin"), CancellationToken.None);
        _ = fake.RunAsync(new WebSocketTransport(socket, Clock, "fake"));
        return fake;
    }

    /// <summary>server/hello followed by the activation a configured MA would send for playback.</summary>
    private static async Task ActivateForPlaybackAsync(FakeSecureServer fake)
    {
        await fake.SendJsonAsync("server/hello", new { name = fake.Name });
        await fake.WaitForAsync("client/hello");
        await fake.SendJsonAsync("server/activate", new { activities = new[] { "playback" }, active_roles = PlaybackRoles });
    }

    [Fact]
    public async Task Unpaired_server_is_welcomed_with_a_full_hello_and_reports_availability_after_clock_sync()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var fake = await ConnectAsync(rig);

        await fake.SendJsonAsync("server/hello", new { name = "Secure MA" });
        var hello = await fake.WaitForAsync("client/hello");
        Assert.Equal("Test PC", hello.GetProperty("name").GetString());
        Assert.True(hello.GetProperty("unpaired_access").GetProperty("enabled").GetBoolean());
        Assert.True(hello.GetProperty("supported_pair_methods").TryGetProperty("pairing_psk", out _));
        var roles = hello.GetProperty("supported_roles").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("player@v1", roles);
        Assert.Contains("controller@v1", roles);

        // before activation nothing may be reported as available
        Assert.Empty(fake.Payloads("client/state"));
        Assert.Empty(fake.Payloads("client/time"));

        await fake.SendJsonAsync("server/activate", new { activities = new[] { "playback" }, active_roles = PlaybackRoles });
        await fake.WaitForAsync("client/time");
        var state = await fake.WaitForAsync("client/state");
        Assert.True(state.GetProperty("available").GetBoolean());
        var player = state.GetProperty("player");
        Assert.Equal(rig.Volume.Volume, player.GetProperty("volume").GetInt32());
        Assert.False(player.GetProperty("muted").GetBoolean());
        Assert.Equal(0, player.GetProperty("output_delay_ms").GetInt32());
        Assert.True(player.GetProperty("required_lead_time_ms").GetInt32() > 0);

        await WaitUntilAsync(() => rig.Service.Status.Connection == ConnectionState.Connected, what: "connected");
        var session = rig.Service.Status.Session;
        Assert.Equal(SessionSecurity.EncryptedUnpaired, session.Security);
        Assert.Equal("Secure MA", session.ServerName);
        Assert.Equal(fake.ServerId, session.ServerId);
    }

    [Fact]
    public async Task Plays_a_stream_at_the_server_timestamp_with_correct_pitch()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var fake = await ConnectAsync(rig);
        await ActivateForPlaybackAsync(fake);
        await fake.WaitForAsync("client/state");
        await WaitUntilAsync(() => fake.Payloads("client/time").Count() >= 24, what: "time bursts");

        await fake.SendJsonAsync("stream/start", new { player = new { codec = "pcm", sample_rate = 48000, channels = 2, bit_depth = 16 } });
        var startTs = fake.ServerNow + 700_000;
        var expectedLocal = startTs - FakeSecureServer.ClockOffset;
        await fake.StreamSineAsync(startTs, 1.5);

        await WaitUntilAsync(() => rig.Output.FirstSoundMicros() is not null, what: "first sound");
        var errorMs = (rig.Output.FirstSoundMicros()!.Value - expectedLocal) / 1000.0;
        Assert.InRange(errorMs, -8, 8);

        await WaitUntilAsync(() => rig.Output.LeftFromFirstSound(48000).Length >= 48000, what: "1 s of audio");
        var left = rig.Output.LeftFromFirstSound(48000);
        var crossings = 0;
        for (var i = 1; i < left.Length; i++)
        {
            if (left[i - 1] < 0 && left[i] >= 0)
            {
                crossings++;
            }
        }

        Assert.InRange(crossings, 436, 444);
        Assert.True(rig.Service.Status.InSync);
    }

    [Fact]
    public async Task Server_commands_drive_volume_mute_and_output_delay_and_are_reported_back()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var delay = -1;
        rig.Service.StaticDelayChanged += d => delay = d;
        var fake = await ConnectAsync(rig);
        await ActivateForPlaybackAsync(fake);
        await fake.WaitForAsync("client/state");

        await fake.SendJsonAsync("server/command", new { player = new { command = "volume", volume = 33 } });
        await WaitUntilAsync(() => rig.Volume.Volume == 33, what: "volume");
        await fake.WaitForAsync("client/state", p => p.TryGetProperty("player", out var pl) && pl.GetProperty("volume").GetInt32() == 33);

        await fake.SendJsonAsync("server/command", new { player = new { command = "mute", mute = true } });
        await WaitUntilAsync(() => rig.Volume.Muted, what: "mute");
        await fake.WaitForAsync("client/state", p => p.TryGetProperty("player", out var pl) && pl.GetProperty("muted").GetBoolean());

        await fake.SendJsonAsync("server/command", new { player = new { command = "set_output_delay", output_delay_ms = 120 } });
        await fake.WaitForAsync("client/state", p => p.TryGetProperty("player", out var pl) && pl.GetProperty("output_delay_ms").GetInt32() == 120);
        Assert.Equal(120, delay);
    }

    [Fact]
    public async Task Changing_the_system_volume_is_reported_to_the_server()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var fake = await ConnectAsync(rig);
        await ActivateForPlaybackAsync(fake);
        await fake.WaitForAsync("client/state");

        rig.Volume.SetVolume(21);
        await fake.WaitForAsync("client/state", p => p.TryGetProperty("player", out var pl) && pl.GetProperty("volume").GetInt32() == 21);
    }

    [Fact]
    public async Task Metadata_group_and_controller_state_flow_into_status()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var fake = await ConnectAsync(rig);
        await ActivateForPlaybackAsync(fake);
        await fake.WaitForAsync("client/state");

        await fake.SendJsonAsync("server/state", new
        {
            metadata = new { timestamp = fake.ServerNow, title = "Song", artist = "Artist", album = "Album" },
            controller = new { supported_commands = new[] { "play", "pause", "next" }, volume = 20, muted = false },
        });
        await fake.SendJsonAsync("group/update", new { playback_state = "playing", group_id = "g1", group_name = "Living room" });

        await WaitUntilAsync(() => rig.Service.Status.Session.Metadata.Title == "Song" && rig.Service.Status.Session.Group.GroupName == "Living room");
        Assert.True(rig.Service.Status.Session.Controller.Supports("next"));

        await rig.Service.SendCommandAsync(new ControllerCommand("next"));
        var command = await fake.WaitForAsync("client/command");
        Assert.Equal("next", command.GetProperty("controller").GetProperty("command").GetString());
    }

    [Fact]
    public async Task Metadata_with_a_future_timestamp_takes_effect_at_that_moment()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var fake = await ConnectAsync(rig);
        await ActivateForPlaybackAsync(fake);
        await fake.WaitForAsync("client/state");
        await WaitUntilAsync(() => fake.Payloads("client/time").Count() >= 8, what: "clock sync");

        await fake.SendJsonAsync("server/state", new { metadata = new { timestamp = fake.ServerNow + 600_000, title = "Later" } });
        await Task.Delay(200);
        Assert.NotEqual("Later", rig.Service.Status.Session.Metadata.Title);
        await WaitUntilAsync(() => rig.Service.Status.Session.Metadata.Title == "Later", what: "scheduled metadata");
    }

    [Fact]
    public async Task Artwork_is_assembled_from_announce_and_parts()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var fake = await ConnectAsync(rig);
        await ActivateForPlaybackAsync(fake);
        await fake.WaitForAsync("client/state");

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6 };
        var announce = new byte[14];
        announce[0] = 8;
        announce[1] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(announce.AsSpan(2, 8), fake.ServerNow);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(announce.AsSpan(10, 4), (uint)png.Length);
        await fake.SendBinaryAsync(announce);
        var firstHalf = new byte[2 + 7];
        firstHalf[0] = 8;
        png.AsSpan(0, 7).CopyTo(firstHalf.AsSpan(2));
        await fake.SendBinaryAsync(firstHalf);
        var secondHalf = new byte[2 + png.Length - 7];
        secondHalf[0] = 8;
        png.AsSpan(7).CopyTo(secondHalf.AsSpan(2));
        await fake.SendBinaryAsync(secondHalf);

        await WaitUntilAsync(() => rig.Service.Status.Session.Artwork is not null, what: "artwork");
        Assert.Equal(png, rig.Service.Status.Session.Artwork!.Data);
    }

    [Fact]
    public async Task Unpaired_access_disabled_refuses_a_playback_activation_with_pairing_required()
    {
        await using var rig = new Rig(new SendspinOptions { PlayerName = "Test PC", ProtocolMode = SendspinProtocolMode.Encrypted, AllowUnpairedAccess = false });
        await rig.StartAsync();
        var fake = await ConnectAsync(rig);
        await ActivateForPlaybackAsync(fake);

        var goodbye = await fake.WaitForAsync("client/goodbye");
        Assert.Equal("pairing_required", goodbye.GetProperty("reason").GetString());
        Assert.NotEqual(ConnectionState.Connected, rig.Service.Status.Connection);
    }

    [Fact]
    public async Task Pairing_under_a_long_term_psk_is_unauthorized()
    {
        await using var rig = new Rig();
        var server = new FakeSecureServer(Clock);
        var longTerm = SendspinPsk.NewRandom();
        rig.Keys.SaveRecord(new PairingRecord(server.ServerId, longTerm));
        server.FirstPsk = PskChoice.LongTerm(longTerm);
        await rig.StartAsync();
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Service.ListenPort}/sendspin"), CancellationToken.None);
        _ = server.RunAsync(new WebSocketTransport(socket, Clock, "fake"));

        await server.SendJsonAsync("server/hello", new { name = server.Name });
        await server.WaitForAsync("client/hello");
        await server.SendJsonAsync("server/activate", new { activities = new[] { "pairing" }, active_roles = Array.Empty<string>(), pairing = new { method = "pairing_psk" } });

        var goodbye = await server.WaitForAsync("client/goodbye");
        Assert.Equal("unauthorized", goodbye.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Playback_is_also_allowed_under_the_pairing_psk_when_unpaired_access_is_on()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var fake = await ConnectAsync(rig, f => f.FirstPsk = PskChoice.Pairing(rig.Keys.PairingPsk));
        await ActivateForPlaybackAsync(fake);
        await WaitUntilAsync(() => rig.Service.Status.Connection == ConnectionState.Connected, what: "connected");
        Assert.Equal(SessionSecurity.EncryptedUnpaired, rig.Service.Status.Session.Security);
    }

    [Fact]
    public async Task Pairing_with_the_pairing_psk_stores_a_long_term_psk_and_rekeys_to_it()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var paired = 0;
        rig.Service.PairingCompleted += () => paired++;
        var fake = await ConnectAsync(rig, f => f.FirstPsk = PskChoice.Pairing(rig.Keys.PairingPsk));

        await fake.SendJsonAsync("server/hello", new { name = "Secure MA" });
        await fake.WaitForAsync("client/hello");
        await fake.SendJsonAsync("server/activate", new { activities = new[] { "pairing" }, active_roles = Array.Empty<string>(), pairing = new { method = "pairing_psk" } });

        var init = await fake.WaitForAsync("client/pair-init");
        Assert.Equal(1, init.GetProperty("pairing_index").GetInt32());
        var finalize = await fake.WaitForAsync("client/pair-finalize");
        var longTerm = Base64Url.Decode(finalize.GetProperty("long_term_psk").GetString()!);
        Assert.Equal(32, longTerm.Length);
        Assert.Empty(rig.Keys.Records); // nothing persisted before the server confirmed

        await fake.SendJsonAsync("server/pair-finalize", new { });
        await WaitUntilAsync(() => rig.Keys.Records.Count == 1 && paired == 1, what: "pairing record saved");
        Assert.Equal(fake.ServerId, rig.Keys.Records[0].ServerId);
        Assert.Equal(longTerm, rig.Keys.Records[0].LongTermPsk);

        await fake.RehandshakeAsync(PskChoice.LongTerm(longTerm));
        await WaitUntilAsync(() => fake.ActiveCategory == PskCategory.LongTerm, what: "rekeyed");
        await fake.SendJsonAsync("server/activate", new { activities = new[] { "playback" }, active_roles = PlaybackRoles });
        await WaitUntilAsync(() => rig.Service.Status.Session.Security == SessionSecurity.EncryptedPaired, what: "paired security");
        await fake.WaitForAsync("client/state");
    }

    [Fact]
    public async Task A_paired_server_connects_with_the_long_term_psk_and_unpair_removes_the_record()
    {
        await using var rig = new Rig();
        var serverKey = new FakeSecureServer(Clock);
        var longTerm = SendspinPsk.NewRandom();
        rig.Keys.SaveRecord(new PairingRecord(serverKey.ServerId, longTerm));
        await rig.StartAsync();

        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Service.ListenPort}/sendspin"), CancellationToken.None);
        serverKey.FirstPsk = PskChoice.LongTerm(longTerm);
        _ = serverKey.RunAsync(new WebSocketTransport(socket, Clock, "fake"));

        await ActivateForPlaybackAsync(serverKey);
        await WaitUntilAsync(() => rig.Service.Status.Session.Security == SessionSecurity.EncryptedPaired, what: "paired");
        Assert.Equal(PskCategory.LongTerm, serverKey.ActiveCategory);

        await serverKey.SendJsonAsync("server/unpair", new { });
        var goodbye = await serverKey.WaitForAsync("client/goodbye");
        Assert.Equal("unpaired", goodbye.GetProperty("reason").GetString());
        Assert.Empty(rig.Keys.Records);
    }

    [Fact]
    public async Task A_pairing_attempt_is_not_displaced_by_a_playback_server_and_declined_with_concurrent_attempt()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var pairing = await ConnectAsync(rig, f => f.FirstPsk = PskChoice.Pairing(rig.Keys.PairingPsk), "Pairing MA");
        await pairing.SendJsonAsync("server/hello", new { name = "Pairing MA" });
        await pairing.WaitForAsync("client/hello");
        await pairing.SendJsonAsync("server/activate", new { activities = new[] { "pairing" }, active_roles = Array.Empty<string>(), pairing = new { method = "pairing_psk" } });
        await pairing.WaitForAsync("client/pair-finalize");

        var other = await ConnectAsync(rig, name: "Other MA");
        await ActivateForPlaybackAsync(other);

        var goodbye = await other.WaitForAsync("client/goodbye");
        Assert.Equal("concurrent_attempt", goodbye.GetProperty("reason").GetString());
        Assert.Empty(pairing.Payloads("client/goodbye"));
    }

    [Fact]
    public async Task Auto_mode_falls_back_to_the_legacy_protocol_for_a_server_that_ignores_the_handshake()
    {
        var options = new SendspinOptions { PlayerName = "Test PC", ProtocolMode = SendspinProtocolMode.Auto, HandshakeTimeout = TimeSpan.FromMilliseconds(400) };
        await using var rig = new Rig(options);
        await rig.StartAsync();

        // first attempt: a legacy server that waits for client/hello and never answers client/init
        var legacy1 = new FakeServer(Clock);
        var socket1 = new ClientWebSocket();
        await socket1.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Service.ListenPort}/sendspin"), CancellationToken.None);
        var run1 = legacy1.RunAsync(new WebSocketTransport(socket1, Clock, "fake"));
        await Task.Delay(1200);
        Assert.NotEqual(ConnectionState.Connected, rig.Service.Status.Connection);

        // the server reconnects (as Music Assistant does) and is now spoken to in the legacy protocol
        var legacy2 = new FakeServer(Clock);
        var socket2 = new ClientWebSocket();
        await socket2.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Service.ListenPort}/sendspin"), CancellationToken.None);
        _ = legacy2.RunAsync(new WebSocketTransport(socket2, Clock, "fake"));
        var hello = await legacy2.WaitForAsync("client/hello");
        Assert.Equal("Test PC", hello.GetProperty("name").GetString());
        await WaitUntilAsync(() => rig.Service.Status.Connection == ConnectionState.Connected, what: "connected via legacy");
        Assert.Equal(SessionSecurity.Plaintext, rig.Service.Status.Session.Security);
        GC.KeepAlive(run1);
    }

    [Fact]
    public async Task Auto_mode_uses_the_encrypted_protocol_for_a_server_that_speaks_it()
    {
        var options = new SendspinOptions { PlayerName = "Test PC", ProtocolMode = SendspinProtocolMode.Auto };
        await using var rig = new Rig(options);
        await rig.StartAsync();
        var fake = await ConnectAsync(rig);
        await ActivateForPlaybackAsync(fake);
        await WaitUntilAsync(() => rig.Service.Status.Connection == ConnectionState.Connected, what: "connected");
        Assert.Equal(SessionSecurity.EncryptedUnpaired, rig.Service.Status.Session.Security);
    }

    [Fact]
    public async Task A_server_that_never_activates_the_player_is_dropped()
    {
        var options = new SendspinOptions { PlayerName = "Test PC", ProtocolMode = SendspinProtocolMode.Encrypted, ActivationTimeout = TimeSpan.FromMilliseconds(300) };
        await using var rig = new Rig(options);
        await rig.StartAsync();
        var fake = new FakeSecureServer(Clock);
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Service.ListenPort}/sendspin"), CancellationToken.None);
        _ = fake.RunAsync(new WebSocketTransport(socket, Clock, "fake"));
        await fake.SendJsonAsync("server/hello", new { name = "Secure MA" });
        await fake.WaitForAsync("client/hello");

        await WaitUntilAsync(() => socket.State != WebSocketState.Open, what: "connection dropped");
        Assert.NotEqual(ConnectionState.Connected, rig.Service.Status.Connection);
    }
}
