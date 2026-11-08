using MaSatellite.Host.Presentation;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;
using System.Text.Json;

namespace MaSatellite.Host.Tests;

public class RedesignLogicTests
{
    private static MaItem Item(string name, MaMediaType type = MaMediaType.Album, int? year = null, DateTimeOffset? added = null,
        DateTimeOffset? played = null, int? plays = null, string[]? genres = null, string? sub = null) =>
        new(type, name, "library", name, "library://x/" + name, sub, null, year, null, null, true, null, genres, added, played, plays);

    [Fact]
    public void Date_sort_orders_by_the_date_not_by_text()
    {
        // As text "9 Jan" > "10 Feb" > "1 Mar"; as dates March is newest.
        var jan = Item("A", added: new DateTimeOffset(2025, 1, 9, 0, 0, 0, TimeSpan.Zero));
        var feb = Item("B", added: new DateTimeOffset(2025, 2, 10, 0, 0, 0, TimeSpan.Zero));
        var mar = Item("C", added: new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var sorted = LibrarySorter.Sort([jan, mar, feb], "added");
        Assert.Equal(["C", "B", "A"], sorted.Select(i => i.Name));
    }

    [Fact]
    public void Missing_dates_sort_last_and_year_is_numeric()
    {
        var none = Item("None");
        var y999 = Item("Old", year: 999);
        var y2000 = Item("New", year: 2000);
        Assert.Equal(["New", "Old", "None"], LibrarySorter.Sort([none, y999, y2000], "year").Select(i => i.Name));
        Assert.Equal(["Old", "New", "None"], LibrarySorter.Sort([none, y2000, y999], "year-asc").Select(i => i.Name));
    }

    [Fact]
    public void Popular_sort_is_numeric_and_can_sort_reports_missing_data()
    {
        var a = Item("A", plays: 9);
        var b = Item("B", plays: 10);
        Assert.Equal(["B", "A"], LibrarySorter.Sort([a, b], "popular").Select(i => i.Name));
        Assert.False(LibrarySorter.CanSort([Item("x")], "added"));
        Assert.True(LibrarySorter.CanSort([a], "popular"));
    }

    [Fact]
    public void Modes_match_names_and_genres_and_never_reuse_a_playlist()
    {
        var focus = Item("Deep Focus", MaMediaType.Playlist);
        var chill = Item("Sunday chill", MaMediaType.Playlist);
        var tagged = Item("Mix 1", MaMediaType.Playlist, genres: ["House"]);
        var picks = ListeningModes.Resolve([focus, chill, tagged]);
        Assert.Equal(["focus", "relax", "party"], picks.Select(p => p.Mode.Key));
        Assert.Equal("Mix 1", picks[2].Playlist.Name);
        Assert.Empty(ListeningModes.Resolve([Item("Zzz", MaMediaType.Playlist)]));
    }

    [Fact]
    public void Genres_are_counted_from_real_tags_only()
    {
        var genres = DiscoverComposer.Genres([Item("a", genres: ["House", "Pop"]), Item("b", genres: ["house"]), Item("c")]);
        Assert.Equal("House", genres[0].Genre);
        Assert.Equal(2, genres[0].Count);
        Assert.Empty(DiscoverComposer.Genres([Item("c")]));
    }

    [Fact]
    public void Discover_has_no_insight_or_stats_without_data()
    {
        var layout = DiscoverComposer.Compose([], [Item("a")], [], [], null, [], null);
        Assert.Null(layout.Insight);
        Assert.Null(layout.Stats);
        Assert.Single(layout.Undiscovered);
        Assert.Empty(layout.NewAlbums);
    }

    [Fact]
    public void Discover_uses_play_counts_for_stats_and_insight()
    {
        var albums = new[] { Item("a", plays: 4, genres: ["Electronic"], added: DateTimeOffset.UnixEpoch.AddDays(5)), Item("b") };
        var artists = new[] { Item("Daft Punk", MaMediaType.Artist, plays: 12) };
        var recent = new[] { Item("t", MaMediaType.Track, sub: "Daft Punk, Pharrell") };
        var layout = DiscoverComposer.Compose(recent, albums, artists, [], null, [], null);
        Assert.Equal(4, layout.Stats!.TotalPlays);
        Assert.Equal(2, layout.Stats.DistinctArtists);
        Assert.Equal("Daft Punk", layout.Stats.TopArtist);
        Assert.Contains("Daft Punk is your most played artist.", layout.Insight!.Detail);
        Assert.Single(layout.NewAlbums);
    }

    [Fact]
    public void Parser_reads_dates_favourite_and_lyrics_from_music_assistant_json()
    {
        using var doc = JsonDocument.Parse("""
            {"media_type":"track","item_id":"1","provider":"library","name":"T","uri":"library://track/1",
             "timestamp_added":1700000000,"last_played":0,"play_count":3,"favorite":true,
             "metadata":{"lyrics":"la la"}}
            """);
        var item = MaItemParser.ParseItem(doc.RootElement, "http://x");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), item.AddedAt);
        Assert.Null(item.LastPlayed);
        Assert.Equal(3, item.PlayCount);
        Assert.True(item.Favorite);
        Assert.Equal("la la", item.Lyrics);
    }

    [Fact]
    public void Greeting_subtitle_names_the_part_of_day()
    {
        Assert.Equal("Here's your music for this afternoon.", Greeting.Subtitle(14));
    }

    [Fact]
    public void Related_artist_needs_an_exact_name_match()
    {
        Assert.Equal("Daft Punk", RelatedArtist.PrimaryName("Daft Punk, Pharrell"));
        Assert.Null(RelatedArtist.PrimaryName("  "));
        var artists = new[] { Item("Daft Punk Tribute", MaMediaType.Artist), Item("daft punk", MaMediaType.Artist) };
        Assert.Equal("daft punk", RelatedArtist.Pick(artists, "Daft Punk")!.Name);
        Assert.Null(RelatedArtist.Pick([Item("Daft Punk Tribute", MaMediaType.Artist)], "Daft Punk"));
    }

    [Fact]
    public void Because_you_played_uses_other_albums_by_the_same_artist()
    {
        var recent = new[] { Item("Discovery", MaMediaType.Album, sub: "Daft Punk") };
        var albums = new[] { Item("Discovery", sub: "Daft Punk"), Item("Homework", sub: "Daft Punk"), Item("Other", sub: "Someone") };
        var because = DiscoverComposer.Because(recent, albums);
        Assert.Equal("Because you played Discovery", because!.Value.Title);
        Assert.Equal(["Homework"], because.Value.Items.Select(i => i.Name));
        Assert.Null(DiscoverComposer.Because([], albums));
    }

    [Fact]
    public void Lrc_lyrics_lose_their_time_tags_but_plain_lyrics_are_untouched()
    {
        var lrc = "[ar:Artist]\n[00:12.30]First line\n[01:02.5][01:30.00]Second line\n";
        Assert.Equal("First line" + Environment.NewLine + "Second line", LyricsFormatter.ToPlainText(lrc));
        Assert.Equal("Just words" + Environment.NewLine + "More", LyricsFormatter.ToPlainText("Just words\nMore"));
    }
}
