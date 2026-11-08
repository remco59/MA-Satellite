using System.Net;
using MaSatellite.Host.NowPlaying;
using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Tests.NowPlaying;

public class AboutServiceTests
{
    private const string RecordingId = "11111111-1111-1111-1111-111111111111";
    private const string ArtistId = "22222222-2222-2222-2222-222222222222";
    private const string ReleaseId = "33333333-3333-3333-3333-333333333333";

    private static MaItemDetails Track(string? mbid = null, string? label = null) => new(
        new MaItem(MaMediaType.Track, "1", "library", "Intoxicated (Radio Edit)", "library://track/1", "Martin Solveig", "Intoxicated", 2015, 158, null, Genres: ["House"]),
        [new MaArtistRef("Martin Solveig", "9", "library", "library://artist/9", null)],
        "Intoxicated", "library://album/2", "2", "library", null, null, mbid, null, null, label, null, null, ["House"], null, []);

    private static StubHttp Mb() => new(u => u.AbsolutePath switch
    {
        var p when p.Contains("/release/") => (HttpStatusCode.OK, $$$"""{"id":"{{{ReleaseId}}}","title":"Intoxicated","label-info":[{"catalog-number":"CAT1","label":{"name":"Mixture Stereophonic"}}]}"""),
        var p when p.EndsWith("/recording", StringComparison.Ordinal) => (HttpStatusCode.OK, $$$"""{"recordings":[{{{RecordingJson}}}]}"""),
        var p when p.Contains("/recording/") => (HttpStatusCode.OK, RecordingJson),
        var p when p.Contains("/artist/") => (HttpStatusCode.OK, $$$"""{"id":"{{{ArtistId}}}","name":"Martin Solveig","type":"Person","country":"FR","life-span":{"begin":"1976","ended":false}}"""),
        _ => (HttpStatusCode.NotFound, "{}"),
    });

    private static readonly string RecordingJson =
        $$$"""{"id":"{{{RecordingId}}}","title":"Intoxicated","length":158000,"first-release-date":"2015-02-02","artist-credit":[{"name":"Martin Solveig","artist":{"id":"{{{ArtistId}}}","name":"Martin Solveig"}}],"genres":[{"name":"house","count":2}],"releases":[{"id":"{{{ReleaseId}}}","title":"Intoxicated","date":"2015-02-02","status":"Official","release-group":{"primary-type":"Single"}}]}""";

    private static AboutService Service(StubHttp http, TempDir dir) =>
        new(new MusicBrainzClient(http.Client(), new JsonDiskCache(dir.Path), minInterval: TimeSpan.Zero));

    [Fact]
    public async Task Structured_musicbrainz_facts_are_verified_and_replace_library_duplicates()
    {
        using var dir = new TempDir();
        var info = await Service(Mb(), dir).BuildAsync(Track(label: "Library Label"), []);

        var label = Assert.Single(info.TrackFacts, f => f.Label == "Record label");
        Assert.Equal("Mixture Stereophonic", label.Value);
        Assert.Equal(InfoTrust.Verified, label.Trust);
        Assert.Equal("MusicBrainz", label.Source);
        Assert.Contains(info.TrackFacts, f => f.Label == "First released" && f.Value == "2015-02-02");
        Assert.Contains(info.TrackFacts, f => f.Label == "Album" && f.Trust == InfoTrust.Library);
        Assert.Contains("matched", info.MatchNote!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith($"{ReleaseId}/front-500", info.ReleaseArtworkUrl, StringComparison.Ordinal);
        var artist = Assert.Single(info.Artists);
        Assert.Contains(artist.Facts, f => f.Label == "Country" && f.Value == "FR");
        Assert.Contains("House", info.Tags);
    }

    [Fact]
    public async Task Library_id_is_used_directly_without_a_search()
    {
        using var dir = new TempDir();
        var http = Mb();
        var info = await Service(http, dir).BuildAsync(Track(mbid: RecordingId), []);

        Assert.DoesNotContain(http.Requests, r => r.AbsolutePath.EndsWith("/recording", StringComparison.Ordinal));
        Assert.Contains("id stored in your library", info.MatchNote);
    }

    [Fact]
    public async Task Without_a_match_only_library_facts_are_shown_and_it_says_so()
    {
        using var dir = new TempDir();
        var http = new StubHttp(_ => (HttpStatusCode.OK, """{"recordings":[],"artists":[]}"""));
        var info = await Service(http, dir).BuildAsync(Track(), []);

        Assert.All(info.TrackFacts, f => Assert.Equal(InfoTrust.Library, f.Trust));
        Assert.Contains("No confident", info.MatchNote);
    }

    [Fact]
    public async Task An_unreachable_musicbrainz_degrades_to_library_facts_with_a_warning()
    {
        using var dir = new TempDir();
        var info = await Service(new StubHttp(_ => (HttpStatusCode.ServiceUnavailable, "x")), dir).BuildAsync(Track(), []);
        Assert.NotEmpty(info.Warnings);
        Assert.NotEmpty(info.TrackFacts);
    }

    [Fact]
    public void Biographies_are_stripped_of_markup_and_cut_at_a_sentence()
    {
        var long_ = string.Join(" ", Enumerable.Repeat("This is a sentence about the artist.", 60));
        var bio = AboutService.Concise($"<a href=\"x\">Link</a> {long_}");
        Assert.DoesNotContain("<", bio);
        Assert.True(bio!.Length <= 700);
        Assert.EndsWith(".", bio);
        Assert.Null(AboutService.Concise("   "));
    }

    [Fact]
    public async Task Artist_biography_is_labelled_as_community_text()
    {
        using var dir = new TempDir();
        var artistDetails = new MaItemDetails(
            new MaItem(MaMediaType.Artist, "9", "library", "Martin Solveig", "library://artist/9", null, null, null, null, null),
            [], null, null, null, null, null, null, null, null, null, null, null, null, [], "French DJ and producer.", []);
        var info = await Service(Mb(), dir).BuildAsync(Track(), [artistDetails]);
        var artist = Assert.Single(info.Artists);
        Assert.Equal("French DJ and producer.", artist.Biography);
        Assert.Contains("community", artist.BiographySource, StringComparison.OrdinalIgnoreCase);
    }
}
