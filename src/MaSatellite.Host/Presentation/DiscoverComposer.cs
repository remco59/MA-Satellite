using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Presentation;

/// <summary>A genre the library actually contains, with how many albums carry it.</summary>
public sealed record GenreCount(string Genre, int Count);

/// <summary>An insight line the data really supports.</summary>
public sealed record MusicInsight(string Headline, string Detail, IReadOnlyList<MaItem> Artists);

/// <summary>Listening numbers computed from the server's own history and play counts. No estimates.</summary>
public sealed record ListeningStats(int RecentItems, int DistinctArtists, int PlayedAlbums, int TotalPlays, string? TopArtist);

public sealed record DiscoverLayout(
    MusicInsight? Insight,
    ListeningStats? Stats,
    IReadOnlyList<GenreCount> Genres,
    IReadOnlyList<MaItem> NewAlbums,
    IReadOnlyList<MaItem> Undiscovered,
    IReadOnlyList<MaItem> SimilarArtists,
    string? SimilarSeed,
    IReadOnlyList<MaItem> BecauseYouPlayed,
    string? BecauseTitle);

/// <summary>
/// Builds Discover from what Music Assistant returned. Every section is derived from real values and is omitted
/// when the data is missing; nothing here is machine-learned, and the page does not claim that it is.
/// </summary>
public static class DiscoverComposer
{
    public static DiscoverLayout Compose(
        IReadOnlyList<MaItem> recent,
        IReadOnlyList<MaItem> albums,
        IReadOnlyList<MaItem> artistsByPlays,
        IReadOnlyList<MaItem> similarArtists,
        string? similarSeed,
        IReadOnlyList<MaItem> becauseItems,
        string? becauseTitle)
    {
        var genres = Genres(albums);
        var stats = Stats(recent, albums, artistsByPlays);
        var insight = Insight(genres, stats, similarArtists, albums);
        var newAlbums = albums.Where(a => a.IsPlayable && !string.IsNullOrWhiteSpace(a.Uri) && a.AddedAt.HasValue)
            .OrderByDescending(a => a.AddedAt).Take(20).ToList();
        // "Not discovered yet": library albums that have never been played (the server reports a play count of 0 or none).
        var undiscovered = albums.Where(a => a.IsPlayable && !string.IsNullOrWhiteSpace(a.Uri)
                && a.LastPlayed is null && (a.PlayCount ?? 0) == 0)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Take(20).ToList();
        return new DiscoverLayout(insight, stats, genres, newAlbums, undiscovered,
            similarArtists.Take(16).ToList(), similarSeed,
            becauseItems.Where(i => i.IsPlayable && !string.IsNullOrWhiteSpace(i.Uri)).Take(16).ToList(), becauseTitle);
    }

    /// <summary>
    /// "Because you played X": the most recent album or track in the history that has an artist, and the other
    /// albums by that same artist in the library. Null when the history or the library has nothing to offer.
    /// </summary>
    public static (string Title, string ArtistName, IReadOnlyList<MaItem> Items)? Because(IReadOnlyList<MaItem> recent, IReadOnlyList<MaItem> albums)
    {
        foreach (var played in recent.Where(i => i.Type is MaMediaType.Album or MaMediaType.Track && !string.IsNullOrWhiteSpace(i.Subtitle)))
        {
            var artist = RelatedArtist.PrimaryName(played.Subtitle)!;
            var more = albums.Where(a => a.IsPlayable && !string.IsNullOrWhiteSpace(a.Uri)
                    && !string.Equals(a.Name, played.AlbumName ?? played.Name, StringComparison.OrdinalIgnoreCase)
                    && a.Subtitle is { } s && s.Contains(artist, StringComparison.CurrentCultureIgnoreCase))
                .Take(16).ToList();
            if (more.Count > 0)
            {
                return ($"Because you played {played.Name}", artist, more);
            }
        }

        return null;
    }

    public static IReadOnlyList<GenreCount> Genres(IEnumerable<MaItem> items) =>
        items.SelectMany(i => i.Genres ?? [])
            .Select(g => g.Trim()).Where(g => g.Length > 0)
            .GroupBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new GenreCount(g.Key, g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Genre, StringComparer.CurrentCultureIgnoreCase)
            .Take(12).ToList();

    public static ListeningStats? Stats(IReadOnlyList<MaItem> recent, IReadOnlyList<MaItem> albums, IReadOnlyList<MaItem> artistsByPlays)
    {
        if (recent.Count == 0 && albums.All(a => (a.PlayCount ?? 0) == 0))
        {
            return null;
        }

        var artists = recent.Where(i => !string.IsNullOrWhiteSpace(i.Subtitle) && i.Type is MaMediaType.Track or MaMediaType.Album)
            .SelectMany(i => i.Subtitle!.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
        var top = artistsByPlays.Where(a => (a.PlayCount ?? 0) > 0).OrderByDescending(a => a.PlayCount).FirstOrDefault()?.Name;
        return new ListeningStats(
            recent.Count,
            artists,
            albums.Count(a => (a.PlayCount ?? 0) > 0),
            albums.Sum(a => a.PlayCount ?? 0),
            top);
    }

    private static MusicInsight? Insight(IReadOnlyList<GenreCount> genres, ListeningStats? stats, IReadOnlyList<MaItem> similar, IReadOnlyList<MaItem> albums)
    {
        var parts = new List<string>();
        if (stats?.TopArtist is { } top)
        {
            parts.Add($"{top} is your most played artist.");
        }

        if (genres.Count > 0)
        {
            var names = genres.Take(2).Select(g => g.Genre.ToLowerInvariant()).ToList();
            parts.Add($"Most of your albums are tagged {string.Join(" and ", names)}.");
        }

        var shown = similar.Take(4).ToList();
        if (parts.Count == 0 && albums.Count > 0)
        {
            // No play counts or genre tags on this server: say what the library itself contains, nothing more.
            var unplayed = albums.Count(a => a.LastPlayed is null && (a.PlayCount ?? 0) == 0);
            parts.Add($"Your library has {albums.Count:N0} albums" + (unplayed > 0 && unplayed < albums.Count
                ? $", {unplayed:N0} of which you haven't played yet."
                : "."));
            shown = albums.Where(a => !string.IsNullOrWhiteSpace(a.ImageUrl)).Take(4).ToList();
        }

        return parts.Count == 0 ? null : new MusicInsight("Your music insight", string.Join(" ", parts), shown);
    }
}
