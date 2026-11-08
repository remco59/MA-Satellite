using System.Net;
using MaSatellite.Host.Infrastructure;
using MaSatellite.Host.Library;
using MaSatellite.Host.NowPlaying;
using MaSatellite.Host.Settings;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Host.Tests.NowPlaying;

public class NowPlayingControllerTests
{
    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDir _dir = new();
        private readonly MusicBrainzClient _mb;

        public Rig(FakeMaServer server, StubHttp http)
        {
            Server = server;
            var secrets = new InMemorySecretStore();
            secrets.Set(SecretNames.MaToken, "secret");
            var settings = new SettingsStore(new AppPaths(_dir.Path), NullLogger<SettingsStore>.Instance);
            settings.Load();
            settings.Save(settings.Current with { MaHost = "127.0.0.1", MaWebPort = server.Port });
            Service = new MaLibraryService(settings, secrets, NullLogger<MaLibraryService>.Instance);
            Library = new LibraryController(Service, settings);
            var cache = new JsonDiskCache(Path.Combine(_dir.Path, "cache"));
            _mb = new MusicBrainzClient(http.Client(), cache, minInterval: TimeSpan.Zero);
            Controller = new NowPlayingController(Library, new LyricsService(http.Client(), cache), new AboutService(_mb));
        }

        public FakeMaServer Server { get; }

        public MaLibraryService Service { get; }

        public LibraryController Library { get; }

        public NowPlayingController Controller { get; }

        public async ValueTask DisposeAsync()
        {
            Controller.Dispose();
            _mb.Dispose();
            await Service.DisposeAsync();
            await Server.DisposeAsync();
            _dir.Dispose();
        }
    }

    private static void SeedTrack(FakeMaServer server) =>
        server.Handlers["music/item_by_uri"] = args => args.GetProperty("uri").GetString() == "library://track/1"
            ? new
            {
                item_id = "1", provider = "library", name = "Idioteque", media_type = "track", uri = "library://track/1", duration = 309,
                artists = new[] { new { name = "Radiohead", item_id = "5", provider = "library", uri = "library://artist/5" } },
                album = new { name = "Kid A" },
                metadata = new { lrc_lyrics = "[00:01.00]a\n[00:02.00]b" },
            }
            : throw new FakeMaServer.FakeError(3, "not found");

    [Fact]
    public async Task Related_sections_report_what_is_unavailable_instead_of_inventing_results()
    {
        await using var rig = new Rig(new FakeMaServer(), new StubHttp(_ => (HttpStatusCode.NotFound, "{}")));
        SeedTrack(rig.Server);
        // Only artist tracks are supported by this "server"; everything else is a missing command.
        rig.Server.Handlers["music/artists/artist_tracks"] = _ => new object[]
        {
            new { item_id = "1", provider = "library", name = "Idioteque", media_type = "track", uri = "library://track/1" },
            new { item_id = "2", provider = "library", name = "Everything In Its Right Place", media_type = "track", uri = "library://track/2" },
        };
        rig.Server.Handlers["music/artists/artist_albums"] = _ => Array.Empty<object>();

        var related = await rig.Controller.GetRelatedAsync("library://track/1");

        var similar = related.Sections.Single(s => s.Title == "Similar tracks");
        Assert.Equal(RelatedStatus.Unavailable, similar.Status);
        Assert.Empty(similar.Items);
        var more = related.Sections.Single(s => s.Title == "More from Radiohead");
        Assert.Equal(RelatedStatus.Ok, more.Status);
        Assert.Equal(["Everything In Its Right Place"], more.Items.Select(i => i.Name));
        Assert.Equal(RelatedStatus.Empty, related.Sections.Single(s => s.Title == "Albums by Radiohead").Status);
    }

    [Fact]
    public async Task Lyrics_come_from_music_assistant_without_touching_the_network()
    {
        var http = new StubHttp(_ => (HttpStatusCode.OK, "{}"));
        await using var rig = new Rig(new FakeMaServer(), http);
        SeedTrack(rig.Server);

        var result = await rig.Controller.GetLyricsAsync(new LyricsQuery("Idioteque", "Radiohead", "Kid A", 309), "library://track/1");

        Assert.Equal(LyricsKind.Synced, result.Kind);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Details_are_fetched_once_per_track()
    {
        await using var rig = new Rig(new FakeMaServer(), new StubHttp(_ => (HttpStatusCode.OK, "{}")));
        SeedTrack(rig.Server);

        await rig.Controller.GetDetailsAsync("library://track/1");
        await rig.Controller.GetDetailsAsync("library://track/1");

        Assert.Equal(1, rig.Server.Received.Count(r => r.Command == "music/item_by_uri"));
    }

    [Fact]
    public async Task Queue_edits_go_to_the_selected_players_queue()
    {
        await using var rig = new Rig(new FakeMaServer(), new StubHttp(_ => (HttpStatusCode.OK, "{}")));
        rig.Server.Handlers["player_queues/delete_item"] = _ => null;
        rig.Server.Handlers["player_queues/clear"] = _ => null;
        rig.Server.Handlers["player_queues/move_item"] = _ => null;
        var target = new PlayTarget("queue-1", "Kitchen", false, true, "playing");

        await rig.Library.RemoveQueueItemAsync(target, "a");
        await rig.Library.MoveQueueItemAsync(target, "b", 3);
        await rig.Library.ClearQueueAsync(target);

        Assert.All(rig.Server.Received.Where(r => r.Command.StartsWith("player_queues/", StringComparison.Ordinal)),
            r => Assert.Equal("queue-1", r.Args.GetProperty("queue_id").GetString()));
        Assert.Equal(3, rig.Server.Received.Count(r => r.Command.StartsWith("player_queues/", StringComparison.Ordinal)));
    }
}
