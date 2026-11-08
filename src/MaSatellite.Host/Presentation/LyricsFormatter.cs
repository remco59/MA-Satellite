using System.Text.RegularExpressions;

namespace MaSatellite.Host.Presentation;

/// <summary>Turns lyrics as Music Assistant stores them (plain text, or LRC with time tags) into readable text.</summary>
public static partial class LyricsFormatter
{
    [GeneratedRegex(@"^\s*(\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\])+\s*", RegexOptions.None)]
    private static partial Regex TimeTags();

    [GeneratedRegex(@"^\s*\[(ar|ti|al|by|offset|length|re|ve):[^\]]*\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    public static string ToPlainText(string lyrics)
    {
        var lines = lyrics.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !MetaTag().IsMatch(l))
            .Select(l => TimeTags().Replace(l, string.Empty).TrimEnd());
        return string.Join(Environment.NewLine, lines).Trim();
    }
}
