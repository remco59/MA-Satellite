using System.Net;
using MaSatellite.Host.NowPlaying;

namespace MaSatellite.Host.Tests.NowPlaying;

public class MusicBrainzClientTests
{
    private const string RecordingId = "11111111-1111-1111-1111-111111111111";
    private const string ArtistId = "22222222-2222-2222-2222-222222222222";
    private const string ReleaseId = "33333333-3333-3333-3333-333333333333";

    private static string Recording(string title = "Intoxicated", string artist = "Martin Solveig", int lengthMs = 158000) =>
        $$$"""
        {"id":"{{{RecordingId}}}","title":"{{{title}}}","length":{{{lengthMs}}},"first-release-date":"2015-02-02",
         "artist-credit":[{"name":"{{{artist}}}","artist":{"id":"{{{ArtistId}}}","name":"{{{artist}}}"}}],
         "genres":[{"name":"house","count":3},{"name":"electro","count":1}],
         "releases":[{"id":"{{{ReleaseId}}}","title":"Intoxicated","date":"2015-02-02","status":"Official","country":"XW","release-group":{"primary-type":"Single"}}]}
        """;

    private static MusicBrainzClient Client(StubHttp http, TempDir dir) =>
        new(http.Client(), new JsonDiskCache(dir.Path), minInterval: TimeSpan.Zero);

    [Fact]
    public async Task Lookup_by_id_parses_the_recording_and_is_cached()
    {
        using var dir = new TempDir();
        var http = new StubHttp(_ => (HttpStatusCode.OK, Recording()));
        using var mb = Client(http, dir);

        var first = await mb.GetRecordingAsync(RecordingId);
        var second = await mb.GetRecordingAsync(RecordingId);

        Assert.NotNull(first);
        Assert.Equal("2015-02-02", first!.FirstReleaseDate);
        Assert.Equal(158, first.LengthSeconds);
        Assert.Equal("house", first.Genres[0]);
        Assert.Equal("Single", first.Releases[0].PrimaryType);
        Assert.NotNull(second);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_value_that_is_not_an_id_never_reaches_the_network()
    {
        using var dir = new TempDir();
        var http = new StubHttp(_ => (HttpStatusCode.OK, "{}"));
        using var mb = Client(http, dir);
        Assert.Null(await mb.GetRecordingAsync("not-a-guid"));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Search_only_returns_a_confident_match()
    {
        using var dir = new TempDir();
        var wrongSong = $$$"""{"recordings":[{{{Recording(title: "Intoxicated", artist: "Different Band", lengthMs: 158000)}}}]}""";
        var http = new StubHttp(_ => (HttpStatusCode.OK, wrongSong));
        using var mb = Client(http, dir);

        Assert.Null(await mb.FindRecordingAsync(new TrackKey("Intoxicated", ["Martin Solveig"], 158)));
    }

    [Fact]
    public async Task Search_returns_the_recording_when_title_artist_and_length_agree()
    {
        using var dir = new TempDir();
        var http = new StubHttp(u => u.AbsolutePath.EndsWith("/recording", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, $$$"""{"recordings":[{{{Recording()}}}]}""")
            : (HttpStatusCode.OK, Recording()));
        using var mb = Client(http, dir);

        var found = await mb.FindRecordingAsync(new TrackKey("Intoxicated (Radio Edit)", ["Martin Solveig"], 158));

        Assert.NotNull(found);
        Assert.Equal(RecordingId, found!.Id);
        Assert.True(found.Confidence >= TrackMatcher.AcceptThreshold);
    }

    [Fact]
    public async Task Server_errors_throw_and_are_not_cached_as_missing()
    {
        using var dir = new TempDir();
        var http = new StubHttp(_ => (HttpStatusCode.ServiceUnavailable, "busy"));
        using var mb = Client(http, dir);
        await Assert.ThrowsAsync<HttpRequestException>(() => mb.GetRecordingAsync(RecordingId));
        await Assert.ThrowsAsync<HttpRequestException>(() => mb.GetRecordingAsync(RecordingId));
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Ambiguous_artist_names_are_not_resolved()
    {
        using var dir = new TempDir();
        var two = $$$"""{"artists":[{"id":"{{{ArtistId}}}","name":"Nirvana","score":100},{"id":"44444444-4444-4444-4444-444444444444","name":"Nirvana","score":100}]}""";
        using var mb = Client(new StubHttp(_ => (HttpStatusCode.OK, two)), dir);
        Assert.Null(await mb.FindArtistIdAsync("Nirvana"));
    }

    [Fact]
    public async Task Artist_lookup_reads_origin_dates_and_links()
    {
        using var dir = new TempDir();
        var json = $$$"""
        {"id":"{{{ArtistId}}}","name":"Martin Solveig","type":"Person","country":"FR",
         "life-span":{"begin":"1976-09-22","ended":false},
         "genres":[{"name":"house","count":5}],
         "relations":[{"type":"official homepage","url":{"resource":"https://example.com"}},{"type":"social network","url":{"resource":"https://x.example"}}]}
        """;
        using var mb = Client(new StubHttp(_ => (HttpStatusCode.OK, json)), dir);
        var artist = await mb.GetArtistAsync(ArtistId);
        Assert.Equal("FR", artist!.Country);
        Assert.Equal("1976-09-22", artist.Begin);
        Assert.False(artist.Ended);
        Assert.Single(artist.Links);
    }
}
