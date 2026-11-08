using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Presentation;

/// <summary>A listening mode backed by one of the user's own playlists (matched by name or genre), never generated.</summary>
public sealed record ListeningMode(string Key, string Label, string Glyph, string Blurb, IReadOnlyList<string> Keywords);

public sealed record ModePick(ListeningMode Mode, MaItem Playlist);

public static class ListeningModes
{
    public static readonly IReadOnlyList<ListeningMode> All =
    [
        new("focus", "Focus", "\uE7F6", "Steady and melodic, to keep you in the zone.",
            ["focus", "study", "work", "concentrat", "deep", "flow", "instrumental", "lofi", "lo-fi"]),
        new("relax", "Relax", "\uE708", "Slow down and unwind.",
            ["relax", "chill", "calm", "ambient", "sleep", "unwind", "acoustic", "mellow", "evening"]),
        new("party", "Party", "\uE945", "High energy for when the room is full.",
            ["party", "dance", "club", "house", "techno", "workout", "energy", "edm", "pump"]),
        new("discover", "Discover", "\uE721", "New and recently added playlists.",
            ["discover", "new", "fresh", "release", "explore"]),
    ];

    /// <summary>
    /// For each mode, the first playable playlist whose name or genres contain one of its keywords.
    /// Each playlist is used for at most one mode; a mode without a match is left out.
    /// </summary>
    public static IReadOnlyList<ModePick> Resolve(IReadOnlyList<MaItem> playlists)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var picks = new List<ModePick>();
        foreach (var mode in All)
        {
            var match = playlists.FirstOrDefault(p =>
                p.IsPlayable && !string.IsNullOrWhiteSpace(p.Uri) && !used.Contains(p.Uri)
                && Matches(mode, p));
            if (match is not null)
            {
                used.Add(match.Uri);
                picks.Add(new ModePick(mode, match));
            }
        }

        return picks;
    }

    private static bool Matches(ListeningMode mode, MaItem item) =>
        mode.Keywords.Any(k => item.Name.Contains(k, StringComparison.OrdinalIgnoreCase)
            || (item.Genres?.Any(g => g.Contains(k, StringComparison.OrdinalIgnoreCase)) ?? false));
}
