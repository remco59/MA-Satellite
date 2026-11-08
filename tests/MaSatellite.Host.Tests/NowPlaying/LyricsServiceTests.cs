using System.Net;
using MaSatellite.Host.NowPlaying;
using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Tests.NowPlaying;

public class LyricsServiceTests
{
    private static readonly LyricsQuery Query = new("Intoxicated", "Martin Solveig", "Intoxicated", 158);

    private static MaItemDetails Details(string? plain = null, string? synced = null) => new(
        new MaItem(MaMediaType.Track, "1", "library", "Intoxicated", "library://track/1", "Martin Solveig", null, null, 158, null),
        [], null, null, null, null, plain, synced, null, null, null, null, null, null, [], null, []);

    private static string Lrclib(string track = "Intoxicated", string artist = "Martin Solveig", double duration = 158, string? synced = "[00:01.00]a\\n[00:02.00]b", string? plain = "a\\nb", bool instrumental = false) =>
        $$"""{"trackName":"{{track}}","artistName":"{{artist}}","duration":{{duration}},"instrumental":{{(instrumental ? "true" : "false")}},"plainLyrics":{{(plain is null ? "null" : "\"" + plain + "\"")}},"syncedLyrics":{{(synced is null ? "null" : "\"" + synced + "\"")}}}""";

    [Fact]
    public async Task Music_assistant_lyrics_win_and_no_request_is_made()
    {
        using var dir = new TempDir();
        var http = new StubHttp(_ => (HttpStatusCode.OK, "{}"));
        var service = new LyricsService(http.Client(), new JsonDiskCache(dir.Path));

        var result = await service.GetAsync(Query, Details(synced: "[00:01.00]x\n[00:02.00]y"));

        Assert.Equal(LyricsKind.Synced, result.Kind);
        Assert.Equal("Music Assistant", result.Source);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Plain_ma_lyrics_with_timestamps_are_treated_as_synced()
    {
        using var dir = new TempDir();
        var service = new LyricsService(new StubHttp(_ => (HttpStatusCode.OK, "{}")).Client(), new JsonDiskCache(dir.Path));
        var result = await service.GetAsync(Query, Details(plain: "[00:01.00]x\n[00:02.00]y"));
        Assert.Equal(LyricsKind.Synced, result.Kind);
        Assert.Equal(2, result.Lines.Count);
    }

    [Fact]
    public async Task Falls_back_to_lrclib_and_caches_the_answer()
    {
        using var dir = new TempDir();
        var http = new StubHttp(u => (HttpStatusCode.OK, Lrclib()));
        var service = new LyricsService(http.Client(), new JsonDiskCache(dir.Path));

        var first = await service.GetAsync(Query, null);
        var second = await service.GetAsync(Query, null);

        Assert.Equal(LyricsKind.Synced, first.Kind);
        Assert.Equal("LRCLIB", first.Source);
        Assert.Equal(LyricsKind.Synced, second.Kind);
        Assert.Single(http.Requests);
        Assert.Contains("duration=158", http.Requests[0].Query);
    }

    [Fact]
    public async Task A_hit_for_a_different_song_is_not_accepted()
    {
        using var dir = new TempDir();
        var http = new StubHttp(u => u.AbsolutePath.EndsWith("/get", StringComparison.Ordinal) ? (HttpStatusCode.OK, Lrclib(track: "Another Song", artist: "Someone Else")) : (HttpStatusCode.OK, "[]"));
        var result = await new LyricsService(http.Client(), new JsonDiskCache(dir.Path)).GetAsync(Query, null);
        Assert.Equal(LyricsKind.None, result.Kind);
    }

    [Fact]
    public async Task Search_fallback_picks_the_duration_matching_entry()
    {
        using var dir = new TempDir();
        var http = new StubHttp(u => u.AbsolutePath.EndsWith("/get", StringComparison.Ordinal)
            ? (HttpStatusCode.NotFound, "{}")
            : (HttpStatusCode.OK, "[" + Lrclib(duration: 300, synced: null, plain: "wrong") + "," + Lrclib(duration: 159, synced: null, plain: "right") + "]"));
        var result = await new LyricsService(http.Client(), new JsonDiskCache(dir.Path)).GetAsync(Query, null);
        Assert.Equal(LyricsKind.Plain, result.Kind);
        Assert.Equal("right", result.PlainText);
    }

    [Fact]
    public async Task Instrumental_tracks_are_reported_as_such()
    {
        using var dir = new TempDir();
        var http = new StubHttp(_ => (HttpStatusCode.OK, Lrclib(synced: null, plain: null, instrumental: true)));
        var result = await new LyricsService(http.Client(), new JsonDiskCache(dir.Path)).GetAsync(Query, null);
        Assert.Equal(LyricsKind.Instrumental, result.Kind);
    }

    [Fact]
    public async Task Missing_lyrics_are_cached_but_server_errors_are_not()
    {
        using var dir = new TempDir();
        var missing = new StubHttp(_ => (HttpStatusCode.NotFound, "[]"));
        var service = new LyricsService(missing.Client(), new JsonDiskCache(dir.Path));
        Assert.Equal(LyricsKind.None, (await service.GetAsync(Query, null)).Kind);
        var requests = missing.Requests.Count;
        Assert.Equal(LyricsKind.None, (await service.GetAsync(Query, null)).Kind);
        Assert.Equal(requests, missing.Requests.Count);

        using var dir2 = new TempDir();
        var broken = new StubHttp(_ => (HttpStatusCode.InternalServerError, "oops"));
        var failing = new LyricsService(broken.Client(), new JsonDiskCache(dir2.Path));
        var error = await failing.GetAsync(Query, null);
        Assert.Equal(LyricsKind.Error, error.Kind);
        await failing.GetAsync(Query, null);
        Assert.Equal(2, broken.Requests.Count);
    }
}
