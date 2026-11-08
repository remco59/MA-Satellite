using MaSatellite.MaApi.Models;

namespace MaSatellite.MaApi.Tests;

public class NowPlayingApiTests
{
    private static async Task<MaClient> ConnectAsync(FakeMaServer server)
    {
        var client = new MaClient("127.0.0.1", server.Port, "secret");
        await client.ConnectAsync();
        return client;
    }

    [Fact]
    public async Task Queue_edit_commands_send_the_expected_arguments()
    {
        await using var server = new FakeMaServer();
        server.Handlers["player_queues/move_item"] = _ => null;
        server.Handlers["player_queues/delete_item"] = _ => null;
        server.Handlers["player_queues/clear"] = _ => null;
        await using var client = await ConnectAsync(server);

        await client.MoveQueueItemAsync("q1", "item-7", -2);
        await client.MoveQueueItemAsync("q1", "item-8", 0);
        await client.DeleteQueueItemAsync("q1", "item-7");
        await client.ClearQueueAsync("q1");

        var moves = server.Received.Where(r => r.Command == "player_queues/move_item").ToList();
        Assert.Equal(-2, moves[0].Args.GetProperty("pos_shift").GetInt32());
        Assert.Equal("item-7", moves[0].Args.GetProperty("queue_item_id").GetString());
        Assert.Equal(0, moves[1].Args.GetProperty("pos_shift").GetInt32());
        Assert.Equal("item-7", server.Received.Single(r => r.Command == "player_queues/delete_item").Args.GetProperty("item_id_or_index").GetString());
        Assert.Equal("q1", server.Received.Single(r => r.Command == "player_queues/clear").Args.GetProperty("queue_id").GetString());
    }

    [Fact]
    public async Task Queue_items_carry_artist_and_album()
    {
        await using var server = new FakeMaServer();
        server.Handlers["player_queues/items"] = _ => new[]
        {
            new
            {
                queue_item_id = "a1",
                name = "Idioteque",
                index = 0,
                duration = 309,
                media_item = new
                {
                    item_id = "99", provider = "library", name = "Idioteque", media_type = "track", uri = "library://track/99",
                    artists = new[] { new { name = "Radiohead" } }, album = new { name = "Kid A" },
                },
            },
        };
        await using var client = await ConnectAsync(server);

        var item = Assert.Single(await client.GetQueueItemsAsync("q1"));

        Assert.Equal("Radiohead", item.Artist);
        Assert.Equal("Kid A", item.Album);
        Assert.Equal("library://track/99", item.Uri);
    }

    [Fact]
    public async Task Queue_items_are_numbered_by_their_place_in_the_queue_not_by_the_server()
    {
        await using var server = new FakeMaServer();
        // Music Assistant sends no usable index: every item would otherwise be number 0.
        server.Handlers["player_queues/items"] = _ => new[]
        {
            new { queue_item_id = "a", name = "One" },
            new { queue_item_id = "b", name = "Two" },
            new { queue_item_id = "c", name = "Three" },
        };
        await using var client = await ConnectAsync(server);

        var items = await client.GetQueueItemsAsync("q1", limit: 50, offset: 10);

        Assert.Equal([10, 11, 12], items.Select(i => i.Index));
    }

    [Fact]
    public async Task Details_expose_lyrics_ids_label_and_credited_artists()
    {
        await using var server = new FakeMaServer();
        server.Handlers["music/item_by_uri"] = _ => new
        {
            item_id = "99", provider = "library", name = "Idioteque", media_type = "track", uri = "library://track/99", duration = 309,
            external_ids = new object[] { new[] { "musicbrainz_recordingid", "rec-1" }, new { type = "isrc", value = "GBAYE0000001" } },
            artists = new[] { new { name = "Radiohead", item_id = "1", provider = "library", uri = "library://artist/1", external_ids = new object[] { new[] { "musicbrainz_artistid", "art-1" } } } },
            album = new { name = "Kid A", item_id = "12", provider = "library", uri = "library://album/12" },
            metadata = new
            {
                lyrics = "plain words",
                lrc_lyrics = "[00:01.00]a\n[00:02.00]b",
                label = "Parlophone",
                genres = new[] { "Electronic" },
                links = new[] { new { type = "website", url = "https://radiohead.com" } },
            },
        };
        await using var client = await ConnectAsync(server);

        var d = await client.GetDetailsAsync("library://track/99");

        Assert.Equal("rec-1", d.MusicBrainzId);
        Assert.Equal("GBAYE0000001", d.Isrc);
        Assert.Equal("Parlophone", d.Label);
        Assert.Equal("[00:01.00]a\n[00:02.00]b", d.SyncedLyrics);
        Assert.Equal("plain words", d.PlainLyrics);
        var artist = Assert.Single(d.Artists);
        Assert.Equal("art-1", artist.MusicBrainzId);
        Assert.Equal("library://artist/1", artist.Uri);
        Assert.Equal("Kid A", d.AlbumName);
        Assert.Contains("Electronic", d.Genres);
        Assert.Single(d.Links);
    }

    [Fact]
    public async Task Details_without_metadata_leave_everything_optional_empty()
    {
        await using var server = new FakeMaServer();
        server.Handlers["music/item_by_uri"] = _ => new { item_id = "1", provider = "x", name = "Bare", media_type = "track", uri = "x://1" };
        await using var client = await ConnectAsync(server);

        var d = await client.GetDetailsAsync("x://1");

        Assert.Null(d.PlainLyrics);
        Assert.Null(d.SyncedLyrics);
        Assert.Null(d.MusicBrainzId);
        Assert.Empty(d.Artists);
        Assert.Empty(d.Genres);
    }

    [Fact]
    public async Task Similar_tracks_and_playlist_add_use_the_server_commands()
    {
        await using var server = new FakeMaServer();
        server.Handlers["music/tracks/similar_tracks"] = _ => new[] { new { item_id = "5", provider = "spotify", name = "Alike", media_type = "track", uri = "spotify://track/5" } };
        server.Handlers["music/playlists/add_playlist_tracks"] = _ => null;
        await using var client = await ConnectAsync(server);

        var similar = await client.GetSimilarTracksAsync("99", "library", 10);
        await client.AddToPlaylistAsync("7", ["library://track/99"]);

        Assert.Equal("Alike", Assert.Single(similar).Name);
        var add = server.Received.Single(r => r.Command == "music/playlists/add_playlist_tracks").Args;
        Assert.Equal("7", add.GetProperty("db_playlist_id").GetString());
        Assert.Equal("library://track/99", add.GetProperty("uris")[0].GetString());
    }

    [Fact]
    public async Task A_server_without_similar_tracks_support_raises_a_normal_error()
    {
        await using var server = new FakeMaServer();
        await using var client = await ConnectAsync(server);
        await Assert.ThrowsAnyAsync<MaException>(() => client.GetSimilarTracksAsync("1", "library"));
    }
}
