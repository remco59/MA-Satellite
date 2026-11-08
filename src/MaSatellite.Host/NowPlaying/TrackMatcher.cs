using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MaSatellite.Host.NowPlaying;

public sealed record TrackKey(string Title, IReadOnlyList<string> Artists, int? DurationSeconds);

/// <summary>
/// Decides whether an external record is the same recording as the playing track. A title alone is never enough:
/// title, artist and (when both are known) duration must agree, so a different song with the same name is rejected.
/// </summary>
public static partial class TrackMatcher
{
    public const double AcceptThreshold = 0.82;

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\b(feat|ft|featuring|with)\b.*$")]
    private static partial Regex Featuring();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlnum();

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = Brackets().Replace(value.ToLowerInvariant(), " ");
        text = Featuring().Replace(text, " ");
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return NonAlnum().Replace(sb.ToString().Replace("'", string.Empty).Replace("\u2019", string.Empty).Replace("&", " and "), " ").Trim();
    }

    /// <summary>1 for identical normalised text, falling with edit distance.</summary>
    public static double Similarity(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0)
        {
            return 0;
        }

        if (x == y)
        {
            return 1;
        }

        return 1.0 - (double)Levenshtein(x, y) / Math.Max(x.Length, y.Length);
    }

    /// <summary>Best artist agreement between the wanted artists and a candidate's artist credits.</summary>
    public static double ArtistSimilarity(IReadOnlyList<string> wanted, IReadOnlyList<string> candidate)
    {
        if (wanted.Count == 0 || candidate.Count == 0)
        {
            return 0;
        }

        var wantedJoined = string.Join(' ', wanted);
        var candidateJoined = string.Join(' ', candidate);
        var joined = Similarity(wantedJoined, candidateJoined);
        var any = wanted.Max(w => candidate.Max(c => Similarity(w, c)));
        return Math.Max(joined, any);
    }

    /// <summary>Overall confidence in [0,1], or 0 when a hard requirement (title, artist, duration) fails.</summary>
    public static double Score(TrackKey wanted, string candidateTitle, IReadOnlyList<string> candidateArtists, int? candidateDurationSeconds)
    {
        var title = Similarity(wanted.Title, candidateTitle);
        var artist = ArtistSimilarity(wanted.Artists, candidateArtists);
        if (title < 0.8 || artist < 0.7)
        {
            return 0;
        }

        double duration = 0.6; // unknown on either side: neither proof nor disproof
        if (wanted.DurationSeconds is { } w && candidateDurationSeconds is { } c)
        {
            var delta = Math.Abs(w - c);
            if (delta > 8)
            {
                return 0;
            }

            duration = delta <= 2 ? 1 : 1 - (delta - 2) / 6.0;
        }

        return 0.5 * title + 0.35 * artist + 0.15 * duration;
    }

    public static bool IsConfident(double score) => score >= AcceptThreshold;

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
