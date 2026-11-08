using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Presentation;

/// <summary>Resolves the artist of the current track to a library artist without guessing.</summary>
public static class RelatedArtist
{
    /// <summary>The first artist of "A, B" (Music Assistant joins artists with a comma), or null when empty.</summary>
    public static string? PrimaryName(string? artists)
    {
        var first = artists?.Split(',', 2)[0].Trim();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    /// <summary>An exact (case-insensitive) name match; a similar-sounding artist is not accepted.</summary>
    public static MaItem? Pick(IEnumerable<MaItem> candidates, string name) =>
        candidates.FirstOrDefault(a => a.Type == MaMediaType.Artist && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
