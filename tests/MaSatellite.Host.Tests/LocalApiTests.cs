using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using MaSatellite.Core.Volume;
using MaSatellite.Host.LocalApi;

namespace MaSatellite.Host.Tests;

public class LocalApiTests
{
    private sealed class FakeSource : ILocalApiSource
    {
        public SatelliteStatus Status { get; set; } = new();

        public long ServerNowMicros { get; set; }

        public IVolumeControl Volume { get; } = new InMemoryVolumeControl();

        public List<ControllerCommand> Commands { get; } = [];

        public event Action<SatelliteStatus>? StatusChanged;

        public Task SendCommandAsync(ControllerCommand command)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }

        public void Raise(SatelliteStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(status);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static SatelliteStatus Playing(string title = "Song") => new()
    {
        Connection = ConnectionState.Connected,
        Volume = 42,
        InSync = true,
        Session = new SessionState
        {
            ServerName = "Music Assistant",
            Metadata = new MetadataState
            {
                Timestamp = 10_000_000,
                Title = title,
                Artist = "Artist",
                Album = "Album",
                Year = 1999,
                Progress = new TrackProgress(1000, 200_000, 1000),
            },
            Group = new GroupState { PlaybackState = GroupPlaybackState.Playing, GroupName = "Living room" },
            Controller = new ControllerState { SupportedCommands = ["play", "pause", "next"], Shuffle = true, Repeat = RepeatMode.All },
            Artwork = new ArtworkImage([0xFF, 0xD8, 0xFF, 1, 2], "jpeg", 7),
        },
    };

    private static async Task<(LocalApiServer Server, FakeSource Source, HttpClient Http, int Port)> StartAsync()
    {
        var source = new FakeSource { Status = Playing(), ServerNowMicros = 12_000_000 };
        var port = FreePort();
        var server = new LocalApiServer(source, port);
        server.Start();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await Task.CompletedTask;
        return (server, source, http, port);
    }

    [Fact]
    public async Task Now_playing_returns_snake_case_json_with_extrapolated_position()
    {
        var (server, _, http, _) = await StartAsync();
        await using var _s = server;
        using var _h = http;

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/now-playing"));
        var r = doc.RootElement;
        Assert.True(r.GetProperty("connected").GetBoolean());
        Assert.Equal("playing", r.GetProperty("playback").GetString());
        Assert.Equal("Song", r.GetProperty("title").GetString());
        Assert.Equal("Artist", r.GetProperty("artist").GetString());
        Assert.Equal(200_000, r.GetProperty("duration_ms").GetInt64());
        Assert.Equal(3000, r.GetProperty("position_ms").GetInt64()); // 1000 + 2 s elapsed since the metadata timestamp
        Assert.Equal("/api/artwork?v=7", r.GetProperty("artwork_url").GetString());
        Assert.Equal(42, r.GetProperty("volume").GetInt32());
        Assert.True(r.GetProperty("shuffle").GetBoolean());
        Assert.Equal("all", r.GetProperty("repeat").GetString());
        Assert.Equal("Living room", r.GetProperty("group").GetString());
        Assert.Contains("next", r.GetProperty("supported_commands").EnumerateArray().Select(e => e.GetString()));
        Assert.True(r.GetProperty("position_updated_at").GetInt64() > 1_700_000_000_000);
    }

    [Fact]
    public async Task Idle_state_when_not_connected()
    {
        var (server, source, http, _) = await StartAsync();
        await using var _s = server;
        using var _h = http;
        source.Status = new SatelliteStatus { Connection = ConnectionState.Listening };

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/now-playing"));
        Assert.Equal("idle", doc.RootElement.GetProperty("playback").GetString());
        Assert.False(doc.RootElement.GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("artwork_url").ValueKind);
    }

    [Fact]
    public async Task Artwork_is_served_with_content_type_or_404()
    {
        var (server, source, http, _) = await StartAsync();
        await using var _s = server;
        using var _h = http;

        var ok = await http.GetAsync("/api/artwork");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/jpeg", ok.Content.Headers.ContentType!.MediaType);
        Assert.Equal([0xFF, 0xD8, 0xFF, 1, 2], await ok.Content.ReadAsByteArrayAsync());

        source.Status = source.Status with { Session = source.Status.Session with { Artwork = null } };
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/artwork")).StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example", false)]
    [InlineData("http://evil.example:80", false)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("http://127.0.0.1:1234", true)]
    [InlineData("null", true)]
    [InlineData("file://", true)]
    [InlineData("", true)]
    public void Origin_rules(string origin, bool allowed) => Assert.Equal(allowed, LocalApiServer.IsAllowedOrigin(origin));

    [Fact]
    public async Task Foreign_origin_and_foreign_host_are_refused()
    {
        var (server, _, http, port) = await StartAsync();
        await using var _s = server;
        using var _h = http;

        using var evil = new HttpRequestMessage(HttpMethod.Get, "/api/now-playing");
        evil.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(evil)).StatusCode);

        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/now-playing");
        rebinding.Headers.Host = $"evil.example:{port}";
        // Refused by our check (403) or by the listener itself before we see it (404 managed / 400 http.sys): never served.
        Assert.False((await http.SendAsync(rebinding)).IsSuccessStatusCode);

        using var local = new HttpRequestMessage(HttpMethod.Get, "/api/now-playing");
        local.Headers.Add("Origin", "http://localhost:3000");
        var ok = await http.SendAsync(local);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("http://localhost:3000", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Preflight_is_answered_for_allowed_origins()
    {
        var (server, _, http, _) = await StartAsync();
        await using var _s = server;
        using var _h = http;

        using var req = new HttpRequestMessage(HttpMethod.Options, "/api/command");
        req.Headers.Add("Origin", "http://localhost:3000");
        var res = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Contains("POST", res.Headers.GetValues("Access-Control-Allow-Methods").Single());
    }

    [Fact]
    public async Task Commands_reach_the_server_and_volume_is_applied_locally()
    {
        var (server, source, http, _) = await StartAsync();
        await using var _s = server;
        using var _h = http;

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/command", new { command = "next" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/command", new { command = "seek", position_ms = 5000 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/command", new { command = "volume", volume = 65 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/command", new { command = "mute", mute = true })).StatusCode);

        Assert.Equal(["next", "seek"], source.Commands.Select(c => c.Command));
        Assert.Equal(5000, source.Commands[1].PositionMs);
        Assert.Equal(65, source.Volume.Volume);
        Assert.True(source.Volume.Muted);
    }

    [Fact]
    public async Task Bad_commands_are_rejected()
    {
        var (server, source, http, _) = await StartAsync();
        await using var _s = server;
        using var _h = http;

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/command", new { command = "format_disk" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/command", new { nothing = 1 })).StatusCode);
        var notJson = await http.PostAsync("/api/command", new StringContent("command=next", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, notJson.StatusCode);
        Assert.Empty(source.Commands);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/nope")).StatusCode);
    }

    [Fact]
    public async Task Events_push_an_initial_state_then_updates_without_polling()
    {
        var (server, source, http, port) = await StartAsync();
        await using var _s = server;
        using var _h = http;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/events"), CancellationToken.None);

        var first = await ReceiveAsync(ws);
        Assert.Equal("state", first.GetProperty("type").GetString());
        Assert.Equal("Song", first.GetProperty("data").GetProperty("title").GetString());

        source.Raise(Playing("Next song"));
        var update = await ReceiveUntilAsync(ws, e => e.GetProperty("type").GetString() == "state");
        Assert.Equal("Next song", update.GetProperty("data").GetProperty("title").GetString());

        // while playing, a progress tick arrives about once a second
        var progress = await ReceiveUntilAsync(ws, e => e.GetProperty("type").GetString() == "progress");
        Assert.True(progress.GetProperty("data").TryGetProperty("position_ms", out _));
        Assert.Equal("playing", progress.GetProperty("data").GetProperty("playback").GetString());
    }

    [Fact]
    public async Task Websocket_from_a_foreign_origin_is_refused()
    {
        var (server, _, http, port) = await StartAsync();
        await using var _s = server;
        using var _h = http;

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://evil.example");
        await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/events"), CancellationToken.None));
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[16 * 1024];
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone();
    }

    private static async Task<JsonElement> ReceiveUntilAsync(ClientWebSocket ws, Func<JsonElement, bool> predicate)
    {
        for (var i = 0; i < 10; i++)
        {
            var e = await ReceiveAsync(ws);
            if (predicate(e))
            {
                return e;
            }
        }

        throw new TimeoutException("expected event never arrived");
    }
}
