using System.Net.WebSockets;
using MaSatellite.Core.Session;
using MaSatellite.Core.Tests.Support;
using MaSatellite.Core.Time;
using MaSatellite.Core.Transport;
using MaSatellite.Core.Volume;

namespace MaSatellite.Core.Tests;

public class SendspinServiceTests
{
    private static readonly ILocalClock Clock = StopwatchClock.Instance;

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
        public Rig()
        {
            Output = new FakeAudioOutput(Clock);
            Volume = new InMemoryVolumeControl();
            Service = new SendspinService(Output, Volume, Clock);
        }

        public FakeAudioOutput Output { get; }

        public InMemoryVolumeControl Volume { get; }

        public SendspinService Service { get; }

        public SendspinOptions Options { get; init; } = new() { PlayerName = "Test PC", ClientId = "11111111-1111-1111-1111-111111111111", ProtocolMode = SendspinProtocolMode.Legacy };

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Output.Dispose();
        }
    }

    /// <summary>Starts the service in listening mode and connects a fake server to it (the server is the WebSocket client).</summary>
    private static async Task<FakeServer> ConnectFakeServerAsync(Rig rig, string id = "fake-server", string name = "Fake MA", string reason = "playback")
    {
        var fake = new FakeServer(Clock, id, name, reason);
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Service.ListenPort}/sendspin"), CancellationToken.None);
        _ = fake.RunAsync(new WebSocketTransport(socket, Clock, "fake"));
        return fake;
    }

    [Fact]
    public async Task Handshake_announces_player_capabilities_and_initial_state()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var fake = await ConnectFakeServerAsync(rig);

        var hello = await fake.WaitForAsync("client/hello");
        Assert.Equal("Test PC", hello.GetProperty("name").GetString());
        Assert.Equal(1, hello.GetProperty("version").GetInt32());
        var roles = hello.GetProperty("supported_roles").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("player@v1", roles);
        Assert.Contains("controller@v1", roles);
        Assert.Contains("metadata@v1", roles);
        Assert.Contains("artwork@v1", roles);
        var support = hello.GetProperty("player@v1_support");
        Assert.True(support.GetProperty("buffer_capacity").GetInt32() > 0);
        Assert.Equal("flac", support.GetProperty("supported_formats")[0].GetProperty("codec").GetString());

        var state = await fake.WaitForAsync("client/state");
        Assert.Equal("synchronized", state.GetProperty("state").GetString());
        var player = state.GetProperty("player");
        Assert.Equal(rig.Volume.Volume, player.GetProperty("volume").GetInt32());
        Assert.False(player.GetProperty("muted").GetBoolean());
        Assert.Equal(0, player.GetProperty("static_delay_ms").GetInt32());
        Assert.True(player.GetProperty("required_lead_time_ms").GetInt32() > 0);
        Assert.True(player.GetProperty("min_buffer_ms").GetInt32() > 0);

        await WaitUntilAsync(() => rig.Service.Status.Connection == ConnectionState.Connected, what: "connected");
        Assert.Equal("Fake MA", rig.Service.Status.ServerName);
    }

    [Fact]
    public async Task Plays_a_stream_at_the_server_timestamp_with_correct_pitch()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var fake = await ConnectFakeServerAsync(rig);
        await fake.WaitForAsync("client/state");
        // wait until our clock has converged, as a real server would see (client/time bursts)
        await WaitUntilAsync(() => fake.Payloads("client/time").Count() >= 24, what: "time bursts");

        await fake.SendAsync("stream/start", new { player = new { codec = "pcm", sample_rate = 48000, channels = 2, bit_depth = 16 } });
        var startTs = fake.ServerNow + 700_000;
        var expectedLocal = startTs - FakeServer.Offset;
        await fake.StreamSineAsync(startTs, 1.5);

        await WaitUntilAsync(() => rig.Output.FirstSoundMicros() is not null, what: "first sound");
        var first = rig.Output.FirstSoundMicros()!.Value;
        var errorMs = (first - expectedLocal) / 1000.0;
        Assert.InRange(errorMs, -8, 8); // sine starts at zero so the first audible sample lags by a few samples

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

        Assert.InRange(crossings, 436, 444); // 440 Hz over 1 s
        Assert.True(rig.Service.Status.InSync);
    }

    [Fact]
    public async Task Metadata_group_and_controller_state_flow_into_status()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var fake = await ConnectFakeServerAsync(rig);
        await fake.WaitForAsync("client/state");

        await fake.SendAsync("server/state", new
        {
            metadata = new { timestamp = fake.ServerNow, title = "Song", artist = "Artist", album = "Album", progress = new { track_progress = 1000, track_duration = 200000, playback_speed = 1000 } },
            controller = new { supported_commands = new[] { "play", "pause", "next" }, volume = 20, muted = false, repeat = "off", shuffle = false },
        });
        await fake.SendAsync("group/update", new { playback_state = "playing", group_id = "g1", group_name = "Living room" });

        await WaitUntilAsync(() => rig.Service.Status.Session.Metadata.Title == "Song" && rig.Service.Status.Session.Group.GroupName == "Living room");
        var s = rig.Service.Status.Session;
        Assert.Equal("Artist", s.Metadata.Artist);
        Assert.Equal(200000, s.Metadata.Progress!.TrackDurationMs);
        Assert.True(s.Controller.Supports("next"));
        Assert.Equal(Core.Protocol.GroupPlaybackState.Playing, s.Group.PlaybackState);
    }

    [Fact]
    public async Task Server_volume_and_mute_commands_drive_the_system_volume_and_are_reported_back()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var fake = await ConnectFakeServerAsync(rig);
        await fake.WaitForAsync("client/state");

        await fake.SendAsync("server/command", new { player = new { command = "volume", volume = 33 } });
        await WaitUntilAsync(() => rig.Volume.Volume == 33, what: "system volume applied");
        await fake.WaitForAsync("client/state", p => p.GetProperty("player").GetProperty("volume").GetInt32() == 33);

        await fake.SendAsync("server/command", new { player = new { command = "mute", mute = true } });
        await WaitUntilAsync(() => rig.Volume.Muted);
        await fake.WaitForAsync("client/state", p => p.GetProperty("player").GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task Changing_the_system_volume_is_reported_to_the_server()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var fake = await ConnectFakeServerAsync(rig);
        await fake.WaitForAsync("client/state");

        rig.Volume.SetVolume(77); // the user moved the Windows slider

        await fake.WaitForAsync("client/state", p => p.GetProperty("player").GetProperty("volume").GetInt32() == 77);
        Assert.Equal(77, rig.Service.Status.Volume);
    }

    [Fact]
    public async Task Static_delay_command_is_applied_reported_and_surfaced()
    {
        await using var rig = new Rig();
        var seen = new List<int>();
        rig.Service.StaticDelayChanged += seen.Add;
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var fake = await ConnectFakeServerAsync(rig);
        await fake.WaitForAsync("client/state");

        await fake.SendAsync("server/command", new { player = new { command = "set_static_delay", static_delay_ms = 250 } });

        await fake.WaitForAsync("client/state", p => p.GetProperty("player").GetProperty("static_delay_ms").GetInt32() == 250);
        Assert.Equal([250], seen);
    }

    [Fact]
    public async Task Controller_commands_are_sent_only_when_the_server_supports_them()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var fake = await ConnectFakeServerAsync(rig);
        await fake.WaitForAsync("client/state");
        await fake.SendAsync("server/state", new { controller = new { supported_commands = new[] { "play", "pause", "next" }, volume = 10, muted = false, repeat = "off", shuffle = false } });
        await WaitUntilAsync(() => rig.Service.Status.Session.Controller.Supports("next"));

        await rig.Service.SendCommandAsync(new Core.Protocol.ControllerCommand("next"));
        await rig.Service.SendCommandAsync(new Core.Protocol.ControllerCommand("shuffle")); // unsupported, must not be sent

        var cmd = await fake.WaitForAsync("client/command");
        Assert.Equal("next", cmd.GetProperty("controller").GetProperty("command").GetString());
        await Task.Delay(150);
        Assert.Single(fake.Payloads("client/command"));
    }

    [Fact]
    public async Task Artwork_is_stored_and_cleared()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var fake = await ConnectFakeServerAsync(rig);
        await fake.WaitForAsync("client/state");

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
        await fake.SendBinaryAsync(8, fake.ServerNow, jpeg);
        await WaitUntilAsync(() => rig.Service.Status.Session.Artwork is not null);
        Assert.Equal("jpeg", rig.Service.Status.Session.Artwork!.Format);
        Assert.Equal(jpeg, rig.Service.Status.Session.Artwork.Data);

        await fake.SendBinaryAsync(8, fake.ServerNow, []);
        await WaitUntilAsync(() => rig.Service.Status.Session.Artwork is null);
    }

    [Fact]
    public async Task Playback_server_takes_over_from_a_discovery_server_and_the_old_one_gets_goodbye()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var a = await ConnectFakeServerAsync(rig, "server-a", "A", "discovery");
        await WaitUntilAsync(() => rig.Service.Status.ServerName == "A", what: "A connected");

        var b = await ConnectFakeServerAsync(rig, "server-b", "B", "discovery");
        var goodbyeB = await b.WaitForAsync("client/goodbye");
        Assert.Equal("another_server", goodbyeB.GetProperty("reason").GetString());
        Assert.Equal("A", rig.Service.Status.ServerName);

        var c = await ConnectFakeServerAsync(rig, "server-c", "C", "playback");
        var goodbyeA = await a.WaitForAsync("client/goodbye");
        Assert.Equal("another_server", goodbyeA.GetProperty("reason").GetString());
        await WaitUntilAsync(() => rig.Service.Status.ServerName == "C", what: "C connected");
        await c.WaitForAsync("client/state");
    }

    [Fact]
    public async Task Connect_mode_connects_out_and_reconnects_after_the_server_drops()
    {
        await using var rig = new Rig();
        var servers = new List<FakeServer>();
        await using var listener = new SendspinListener(0, "/sendspin", t =>
        {
            var fake = new FakeServer(Clock);
            lock (servers)
            {
                servers.Add(fake);
            }

            return fake.RunAsync(t);
        }, Clock);
        listener.Start();

        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { Mode = ConnectionMode.Connect, ServerHost = "127.0.0.1", ServerPort = listener.Port });
        await WaitUntilAsync(() => rig.Service.Status.Connection == ConnectionState.Connected, what: "first connection");
        FakeServer first;
        lock (servers)
        {
            first = servers[0];
        }

        await first.CloseAsync();

        await WaitUntilAsync(() => { lock (servers) { return servers.Count >= 2; } }, timeoutMs: 10000, what: "reconnect");
        await WaitUntilAsync(() => rig.Service.Status.Connection == ConnectionState.Connected, what: "reconnected");
    }

    [Fact]
    public async Task Stream_end_stops_the_output_and_a_new_stream_restarts_it()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var fake = await ConnectFakeServerAsync(rig);
        await fake.WaitForAsync("client/state");

        await fake.SendAsync("stream/start", new { player = new { codec = "pcm", sample_rate = 48000, channels = 2, bit_depth = 16 } });
        await WaitUntilAsync(() => rig.Output.Starts == 1, what: "output started");
        await fake.SendAsync("stream/end", new { });
        await WaitUntilAsync(() => !rig.Service.Status.Session.StreamActive, what: "stream ended");

        await fake.SendAsync("stream/start", new { player = new { codec = "pcm", sample_rate = 44100, channels = 2, bit_depth = 16 } });
        await WaitUntilAsync(() => rig.Output.Starts == 2, what: "output restarted");
    }

    [Theory]
    [InlineData("server/error")]
    [InlineData("server/init")]
    [InlineData("noise/handshake")]
    public async Task A_server_that_wants_the_newer_encrypted_protocol_is_reported_clearly(string type)
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(rig.Options, new ServerConnectionOptions { ListenPort = 0 });
        var fake = new FakeServer(Clock) { RejectWith = type };
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Service.ListenPort}/sendspin"), CancellationToken.None);
        _ = fake.RunAsync(new WebSocketTransport(socket, Clock, "fake"));

        await WaitUntilAsync(() => rig.Service.Status.Detail == SendspinService.IncompatibleServerMessage, what: "mismatch reported");
        Assert.NotEqual(ConnectionState.Connected, rig.Service.Status.Connection);
        Assert.Equal(ConnectionState.Listening, rig.Service.Status.Connection);
    }

    [Fact]
    public async Task Listener_rejects_non_websocket_requests_and_wrong_paths()
    {
        await using var listener = new SendspinListener(0, "/sendspin", _ => Task.CompletedTask, Clock);
        listener.Start();
        using var http = new HttpClient();

        var wrongPath = await http.GetAsync($"http://127.0.0.1:{listener.Port}/other");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, wrongPath.StatusCode);
        var plain = await http.GetAsync($"http://127.0.0.1:{listener.Port}/sendspin");
        Assert.Equal(System.Net.HttpStatusCode.UpgradeRequired, plain.StatusCode);
    }
}
