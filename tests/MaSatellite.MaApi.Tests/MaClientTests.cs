using System.Text.Json;
using MaSatellite.MaApi.Models;

namespace MaSatellite.MaApi.Tests;

public class MaClientTests
{
    private static readonly object Album = new
    {
        item_id = "12",
        provider = "library",
        name = "Kid A",
        media_type = "album",
        uri = "library://album/12",
        year = 2000,
        artists = new[] { new { name = "Radiohead", item_id = "1", provider = "library" } },
        metadata = new { images = new object[] { new { type = "thumb", path = "/art/kida.jpg", provider = "filesystem", remotely_accessible = false, proxy_id = "abc123" } } },
    };

    private static readonly object Track = new
    {
        item_id = "99",
        provider = "spotify",
        name = "Idioteque",
        version = "Remastered",
        media_type = "track",
        uri = "spotify://track/99",
        duration = 309,
        artists = new[] { new { name = "Radiohead" }, new { name = "Guest" } },
        album = new { name = "Kid A", image = new { type = "thumb", path = "https://img.example/kida.jpg", provider = "spotify", remotely_accessible = true } },
    };

    private static async Task<MaClient> ConnectAsync(FakeMaServer server, string? token = "secret")
    {
        var client = new MaClient("127.0.0.1", server.Port, token);
        await client.ConnectAsync();
        return client;
    }

    [Fact]
    public async Task Connects_authenticates_and_reads_server_info()
    {
        await using var server = new FakeMaServer();
        await using var client = await ConnectAsync(server);

        Assert.True(client.IsConnected);
        Assert.Equal("2.10.8", client.ServerInfo!.ServerVersion);
        Assert.Equal(30, client.ServerInfo.SchemaVersion);
        Assert.Contains(server.Received, r => r.Command == "auth" && r.Args.GetProperty("token").GetString() == "secret");
    }

    [Fact]
    public async Task Missing_or_wrong_token_is_an_auth_error()
    {
        await using var server = new FakeMaServer();
        await Assert.ThrowsAsync<MaAuthException>(async () => await ConnectAsync(server, token: null));
        await Assert.ThrowsAsync<MaAuthException>(async () => await ConnectAsync(server, token: "nope"));
    }

    [Fact]
    public async Task Old_servers_without_auth_connect_without_a_token()
    {
        await using var server = new FakeMaServer(schemaVersion: 27, requiredToken: null);
        await using var client = await ConnectAsync(server, token: null);
        Assert.True(client.IsConnected);
        Assert.DoesNotContain(server.Received, r => r.Command == "auth");
    }

    [Fact]
    public async Task Unreachable_server_is_a_clear_error()
    {
        var client = new MaClient("127.0.0.1", 1, "x");
        var ex = await Assert.ThrowsAsync<MaException>(() => client.ConnectAsync());
        Assert.Contains("Cannot connect", ex.Message);
    }

    [Fact]
    public async Task Search_returns_typed_results_with_image_urls()
    {
        await using var server = new FakeMaServer();
        server.Handlers["music/search"] = _ => new { artists = Array.Empty<object>(), albums = new[] { Album }, tracks = new[] { Track }, playlists = Array.Empty<object>() };
        await using var client = await ConnectAsync(server);

        var results = await client.SearchAsync("kid a");

        var req = server.Received.Last(r => r.Command == "music/search").Args;
        Assert.Equal("kid a", req.GetProperty("search_query").GetString());
        Assert.Equal(["artist", "album", "track", "playlist"], req.GetProperty("media_types").EnumerateArray().Select(e => e.GetString()));

        var album = Assert.Single(results.Albums);
        Assert.Equal("Kid A", album.Name);
        Assert.Equal("Radiohead", album.Subtitle);
        Assert.Equal(2000, album.Year);
        Assert.Equal("library://album/12", album.Uri);
        Assert.Equal($"http://127.0.0.1:{server.Port}/imageproxy/abc123?size=256", album.ImageUrl);

        var track = Assert.Single(results.Tracks);
        Assert.Equal("Idioteque (Remastered)", track.Name);
        Assert.Equal("Radiohead, Guest", track.Subtitle);
        Assert.Equal("Kid A", track.AlbumName);
        Assert.Equal(309, track.DurationSeconds);
        Assert.StartsWith("https://images.weserv.nl/?url=https%3A%2F%2Fimg.example%2Fkida.jpg&w=256", track.ImageUrl);
        Assert.Equal("Radiohead, Guest · Kid A", track.Description);
    }

    [Fact]
    public async Task Blank_search_does_not_hit_the_server()
    {
        await using var server = new FakeMaServer();
        await using var client = await ConnectAsync(server);
        Assert.True((await client.SearchAsync("  ")).IsEmpty);
        Assert.DoesNotContain(server.Received, r => r.Command == "music/search");
    }

    [Fact]
    public async Task Library_listing_sends_paging_and_omits_empty_search()
    {
        await using var server = new FakeMaServer();
        server.Handlers["music/albums/library_items"] = _ => new[] { Album };
        await using var client = await ConnectAsync(server);

        var items = await client.GetLibraryAsync(MaMediaType.Album, search: "", limit: 50, offset: 100);

        Assert.Single(items);
        var args = server.Received.Last(r => r.Command == "music/albums/library_items").Args;
        Assert.Equal(50, args.GetProperty("limit").GetInt32());
        Assert.Equal(100, args.GetProperty("offset").GetInt32());
        Assert.False(args.TryGetProperty("search", out _));
    }

    [Fact]
    public async Task Library_sorting_is_sent_to_music_assistant_instead_of_sorting_only_loaded_items()
    {
        await using var server = new FakeMaServer();
        server.Handlers["music/playlists/library_items"] = _ => Array.Empty<object>();
        await using var client = await ConnectAsync(server);

        await client.GetLibraryAsync(MaMediaType.Playlist, sortField: "play_count", sortDirection: "desc");
        var args = server.Received.Last(r => r.Command == "music/playlists/library_items").Args;
        Assert.Equal("play_count", args.GetProperty("sort_field").GetString());
        Assert.Equal("desc", args.GetProperty("sort_direction").GetString());

        await client.GetLibraryAsync(MaMediaType.Playlist);
        var defaultArgs = server.Received.Last(r => r.Command == "music/playlists/library_items").Args;
        Assert.False(defaultArgs.TryGetProperty("sort_field", out _));
        Assert.False(defaultArgs.TryGetProperty("sort_direction", out _));
    }

    [Fact]
    public async Task Similar_artists_uses_real_provider_results()
    {
        await using var server = new FakeMaServer();
        server.Handlers["music/artists/similar_artists"] = _ => new[]
        {
            new { item_id = "8", provider = "music", name = "Related artist", media_type = "artist", uri = "music://artist/8" },
        };
        await using var client = await ConnectAsync(server);
        var seed = new MaItem(MaMediaType.Artist, "4", "library", "Seed", "library://artist/4", null, null, null, null, null);

        var similar = await client.GetSimilarArtistsAsync(seed, limit: 8);

        Assert.Single(similar);
        Assert.Equal("Related artist", similar[0].Name);
        var args = server.Received.Last(r => r.Command == "music/artists/similar_artists").Args;
        Assert.Equal("4", args.GetProperty("item_id").GetString());
        Assert.Equal(8, args.GetProperty("limit").GetInt32());
        Assert.Equal("library", args.GetProperty("provider_instance_id_or_domain").GetString());
    }

    [Fact]
    public async Task Chunked_results_are_concatenated()
    {
        await using var server = new FakeMaServer();
        server.Handlers["music/albums/library_items"] = _ => Enumerable.Range(1, 7).Select(i => new { item_id = i.ToString(), provider = "library", name = "Album " + i, media_type = "album", uri = "library://album/" + i }).ToList();
        server.ChunkSizes["music/albums/library_items"] = 3;
        await using var client = await ConnectAsync(server);

        var items = await client.GetLibraryAsync(MaMediaType.Album);

        Assert.Equal(7, items.Count);
        Assert.Equal("Album 7", items[^1].Name);
    }

    [Fact]
    public async Task Children_of_albums_playlists_and_artists_use_the_right_commands()
    {
        await using var server = new FakeMaServer();
        server.Handlers["music/albums/album_tracks"] = _ => new[] { Track };
        server.Handlers["music/playlists/playlist_tracks"] = _ => new[] { Track };
        server.Handlers["music/artists/artist_albums"] = _ => new[] { Album };
        await using var client = await ConnectAsync(server);

        MaItem Item(MaMediaType t) => new(t, "5", "library", "x", "library://x/5", null, null, null, null, null);
        Assert.Single(await client.GetChildrenAsync(Item(MaMediaType.Album)));
        Assert.Single(await client.GetChildrenAsync(Item(MaMediaType.Playlist)));
        Assert.Single(await client.GetChildrenAsync(Item(MaMediaType.Artist)));
        var args = server.Received.Last(r => r.Command == "music/albums/album_tracks").Args;
        Assert.Equal("5", args.GetProperty("item_id").GetString());
        Assert.Equal("library", args.GetProperty("provider_instance_id_or_domain").GetString());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetChildrenAsync(Item(MaMediaType.Track)));
    }

    [Fact]
    public async Task Players_and_queues_are_parsed()
    {
        await using var server = new FakeMaServer();
        server.Handlers["players/all"] = _ => new object[]
        {
            new { player_id = "sendspin-abc", name = "Desk PC", available = true, powered = true, playback_state = "playing", type = "player", volume_level = 35 },
            new { player_id = "kitchen", name = "Kitchen", available = false, playback_state = "idle", type = "player" },
        };
        server.Handlers["player_queues/all"] = _ => new object[]
        {
            new { queue_id = "sendspin-abc", display_name = "Desk PC", active = true, available = true, items = 12, current_index = 3, state = "playing", shuffle_enabled = true, repeat_mode = "all", elapsed_time = 42.5,
                  current_item = new { queue_id = "sendspin-abc", queue_item_id = "q1", name = "Idioteque", duration = 309, index = 3, image = new { type = "thumb", path = "/x.jpg", provider = "fs", proxy_id = "pid" } } },
        };
        await using var client = await ConnectAsync(server);

        var players = await client.GetPlayersAsync();
        Assert.Equal(2, players.Count);
        Assert.Equal(35, players[0].Volume);
        Assert.False(players[1].Available);

        var queue = Assert.Single(await client.GetQueuesAsync());
        Assert.Equal(12, queue.ItemCount);
        Assert.Equal(3, queue.CurrentIndex);
        Assert.True(queue.Shuffle);
        Assert.Equal("all", queue.Repeat);
        Assert.Equal("Idioteque", queue.CurrentItem!.Name);
        Assert.Equal($"http://127.0.0.1:{server.Port}/imageproxy/pid?size=128", queue.CurrentItem.ImageUrl);
    }

    [Fact]
    public async Task Play_media_and_transfer_send_the_expected_arguments()
    {
        await using var server = new FakeMaServer();
        server.Handlers["player_queues/play_media"] = _ => null;
        server.Handlers["player_queues/transfer"] = _ => null;
        server.Handlers["player_queues/next"] = _ => null;
        await using var client = await ConnectAsync(server);

        await client.PlayMediaAsync("sendspin-abc", "library://album/12", MaQueueOption.Replace);
        await client.PlayMediaAsync("sendspin-abc", "library://track/1", MaQueueOption.Next);
        await client.TransferQueueAsync("kitchen", "sendspin-abc", autoPlay: true);
        await client.QueueCommandAsync("sendspin-abc", "next");

        var play = server.Received.Where(r => r.Command == "player_queues/play_media").ToList();
        Assert.Equal("replace", play[0].Args.GetProperty("option").GetString());
        Assert.Equal("next", play[1].Args.GetProperty("option").GetString());
        Assert.Equal("library://album/12", play[0].Args.GetProperty("media").GetString());
        Assert.Equal("sendspin-abc", play[0].Args.GetProperty("queue_id").GetString());
        var transfer = server.Received.Single(r => r.Command == "player_queues/transfer").Args;
        Assert.Equal("kitchen", transfer.GetProperty("source_queue_id").GetString());
        Assert.Equal("sendspin-abc", transfer.GetProperty("target_queue_id").GetString());
        Assert.True(transfer.GetProperty("auto_play").GetBoolean());
        Assert.Contains(server.Received, r => r.Command == "player_queues/next");
    }

    [Fact]
    public async Task Command_errors_surface_with_code_and_details()
    {
        await using var server = new FakeMaServer();
        server.Handlers["player_queues/play_media"] = _ => throw new FakeMaServer.FakeError(7, "Media not found");
        await using var client = await ConnectAsync(server);

        var ex = await Assert.ThrowsAsync<MaCommandException>(() => client.PlayMediaAsync("q", "library://x/1"));
        Assert.Equal(7, ex.ErrorCode);
        Assert.Contains("Media not found", ex.Message);
        Assert.Equal("player_queues/play_media", ex.Command);
    }

    [Fact]
    public async Task Server_events_are_raised_and_disconnect_is_reported()
    {
        await using var server = new FakeMaServer();
        await using var client = await ConnectAsync(server);
        var events = new List<MaEvent>();
        var disconnected = new TaskCompletionSource();
        client.EventReceived += events.Add;
        client.Disconnected += _ => disconnected.TrySetResult();

        await server.SendEventAsync("queue_updated", "sendspin-abc", new { queue_id = "sendspin-abc", state = "paused" });
        await WaitAsync(() => events.Count == 1);
        Assert.Equal("queue_updated", events[0].Event);
        Assert.Equal("sendspin-abc", events[0].ObjectId);
        Assert.Equal("paused", events[0].Data.GetProperty("state").GetString());

        await server.CloseSocketAsync();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Commands_after_disconnect_fail_cleanly()
    {
        await using var server = new FakeMaServer();
        await using var client = await ConnectAsync(server);
        await client.DisposeAsync();
        await Assert.ThrowsAsync<MaException>(() => client.GetPlayersAsync());
    }

    [Fact]
    public void Finds_this_pc_by_client_id_then_by_name()
    {
        var players = new[]
        {
            new MaPlayer("kitchen", "Kitchen", true, true, "idle", "player", null),
            new MaPlayer("sendspin-1111-2222", "Desk PC", true, true, "idle", "player", null),
        };

        Assert.Equal("sendspin-1111-2222", MaClient.FindThisPlayer(players, "1111-2222", "Other")!.PlayerId);
        Assert.Equal("kitchen", MaClient.FindThisPlayer(players, "zzz", "kitchen")!.PlayerId);
        Assert.Null(MaClient.FindThisPlayer(players, "zzz", "nope"));
    }

    [Fact]
    public void Legacy_image_proxy_url_is_double_encoded()
    {
        using var doc = JsonDocument.Parse("""{"type":"thumb","path":"/music/a b/cover.jpg","provider":"fs1"}""");
        var url = MaItemParser.ImageUrlFromImage(doc.RootElement, "http://ma:8095/", 64);
        Assert.Equal("http://ma:8095/imageproxy?path=%252Fmusic%252Fa%2520b%252Fcover.jpg&provider=fs1&size=64", url);
    }

    private static async Task WaitAsync(Func<bool> cond)
    {
        var end = Environment.TickCount64 + 5000;
        while (!cond() && Environment.TickCount64 < end)
        {
            await Task.Delay(10);
        }

        Assert.True(cond());
    }
}
