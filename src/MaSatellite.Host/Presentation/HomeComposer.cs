using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Presentation;

/// <summary>Why a playlist is featured. Each kind is a claim we can back with real library data.</summary>
public enum FeaturedKind
{
    /// <summary>One of the user's own playlists whose name fits the current time of day.</summary>
    ForThisMoment,

    /// <summary>The user's most played playlist, ranked by the server's play count.</summary>
    YourRotation,

    /// <summary>The newest playlist in the library.</summary>
    RecentlyAdded,

    /// <summary>A playlist from the library when no ranking is available.</summary>
    FromYourPlaylists,
}

public sealed record FeaturedPick(FeaturedKind Kind, MaItem Item);

public sealed record HomeLayout(
    FeaturedPick? FirstFeatured,
    FeaturedPick? SecondFeatured,
    IReadOnlyList<MaItem> Rotation,
    IReadOnlyList<MaItem> Added)
{
    public IEnumerable<FeaturedPick> Featured =>
        new[] { FirstFeatured, SecondFeatured }.OfType<FeaturedPick>();
}

/// <summary>
/// Decides what Home shows without inventing anything: every pick comes from playlists the server returned,
/// and the same playlist is never shown in two of these sections.
/// </summary>
public static class HomeComposer
{
    public static HomeLayout Compose(
        IReadOnlyList<MaItem> playlistsByPlays,
        bool playlistsRanked,
        IReadOnlyList<MaItem> timePicks,
        IReadOnlyList<MaItem> newestPlaylists,
        int rotationCount = 20,
        int addedCount = 20)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool Usable(MaItem i) => i.IsPlayable && !string.IsNullOrWhiteSpace(i.Uri);

        bool Claim(MaItem i) => Usable(i) && seen.Add(i.Uri);

        FeaturedPick? first = null;
        var moment = timePicks.FirstOrDefault(Claim);
        if (moment is not null)
        {
            first = new FeaturedPick(FeaturedKind.ForThisMoment, moment);
        }
        else
        {
            var newest = newestPlaylists.FirstOrDefault(Claim);
            if (newest is not null)
            {
                first = new FeaturedPick(FeaturedKind.RecentlyAdded, newest);
            }
        }

        FeaturedPick? second = null;
        var top = playlistsByPlays.FirstOrDefault(Claim);
        if (top is not null)
        {
            second = new FeaturedPick(playlistsRanked ? FeaturedKind.YourRotation : FeaturedKind.FromYourPlaylists, top);
        }

        var rotation = playlistsByPlays.Where(Claim).Take(rotationCount).ToList();
        var added = newestPlaylists.Where(Claim).Take(addedCount).ToList();
        return new HomeLayout(first, second, rotation, added);
    }
}
