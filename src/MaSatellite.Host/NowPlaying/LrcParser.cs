using System.Globalization;
using System.Text.RegularExpressions;

namespace MaSatellite.Host.NowPlaying;

public sealed record LyricLine(double StartSeconds, string Text);

/// <summary>Parses LRC (time-stamped lyrics) and finds the line to highlight for a playback position.</summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    /// <summary>True when the text carries at least two time stamps, i.e. it is worth treating as synchronised.</summary>
    public static bool LooksSynced(string? text) => !string.IsNullOrEmpty(text) && Stamp().Count(text) >= 2;

    /// <summary>Returns lines ordered by time. Metadata tags such as [ar:], [ti:] are ignored; [offset:] is honoured.</summary>
    public static IReadOnlyList<LyricLine> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = new List<LyricLine>();
        var offsetSeconds = 0.0;
        var split = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();

        // The offset tag normally sits in the header but applies to the whole file, wherever it appears.
        // Per the LRC convention a positive offset makes lyrics appear sooner, so it is subtracted.
        foreach (var line in split)
        {
            if (OffsetTag().Match(line) is { Success: true } off)
            {
                offsetSeconds = int.Parse(off.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0;
            }
        }

        foreach (var line in split)
        {
            var stamps = Stamp().Matches(line);
            if (stamps.Count == 0)
            {
                continue;
            }

            var lyric = Stamp().Replace(line, string.Empty).Trim();
            foreach (Match m in stamps)
            {
                var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = m.Groups[3].Success
                    ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / Math.Pow(10, m.Groups[3].Length)
                    : 0;
                lines.Add(new LyricLine(Math.Max(0, minutes * 60 + seconds + fraction - offsetSeconds), lyric));
            }
        }

        return lines.OrderBy(l => l.StartSeconds).ToList();
    }

    /// <summary>Index of the line being sung at <paramref name="positionSeconds"/>, or -1 before the first line.</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, double positionSeconds)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].StartSeconds <= positionSeconds)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>Strips time stamps so a synchronised lyric can still be shown as plain text.</summary>
    public static string ToPlainText(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n')
            .Where(l => !OffsetTag().IsMatch(l.Trim()))
            .Select(l => Stamp().Replace(l, string.Empty).Trim()));
}
