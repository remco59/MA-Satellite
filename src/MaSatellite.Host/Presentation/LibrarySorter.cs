using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Presentation;

/// <summary>
/// Client-side ordering of library items by their real values (dates, years, counts), never by their text.
/// Used when the server cannot sort, and to keep missing values at the end instead of scattering them.
/// </summary>
public static class LibrarySorter
{
    public static IReadOnlyList<MaItem> Sort(IEnumerable<MaItem> items, string sort)
    {
        var list = items.ToList();
        return sort switch
        {
            "name-asc" => list.OrderBy(Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "name-desc" => list.OrderByDescending(Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            // Descending by an actual date: items without a date sort last, ties fall back to the name.
            "added" => NewestFirst(list, i => i.AddedAt),
            "played" => NewestFirst(list, i => i.LastPlayed),
            "popular" => list.OrderByDescending(i => i.PlayCount.HasValue).ThenByDescending(i => i.PlayCount)
                .ThenBy(Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "year" => list.OrderByDescending(i => i.Year.HasValue).ThenByDescending(i => i.Year)
                .ThenBy(Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "year-asc" => list.OrderByDescending(i => i.Year.HasValue).ThenBy(i => i.Year)
                .ThenBy(Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => list,
        };
    }

    /// <summary>True when every item carries the value the sort needs, so a client-side sort is trustworthy.</summary>
    public static bool CanSort(IReadOnlyCollection<MaItem> items, string sort) => sort switch
    {
        "default" or "name-asc" or "name-desc" => true,
        "added" => items.Any(i => i.AddedAt.HasValue),
        "played" => items.Any(i => i.LastPlayed.HasValue),
        "popular" => items.Any(i => i.PlayCount.HasValue),
        "year" or "year-asc" => items.Any(i => i.Year.HasValue),
        _ => false,
    };

    private static string Name(MaItem item) => item.Name;

    private static List<MaItem> NewestFirst(List<MaItem> list, Func<MaItem, DateTimeOffset?> date) =>
        list.OrderByDescending(i => date(i).HasValue).ThenByDescending(i => date(i))
            .ThenBy(Name, StringComparer.CurrentCultureIgnoreCase).ToList();
}
