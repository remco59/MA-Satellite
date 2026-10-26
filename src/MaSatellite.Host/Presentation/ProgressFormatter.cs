using MaSatellite.Core.Protocol;

namespace MaSatellite.Host.Presentation;

public static class ProgressFormatter
{
    /// <summary>"3:07" or "1:02:03".</summary>
    public static string Time(long milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(milliseconds, 0));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>"Artist — Title", tolerant of missing fields.</summary>
    public static string Headline(MetadataState m)
    {
        if (string.IsNullOrEmpty(m.Title))
        {
            return string.Empty;
        }

        return string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Artist} — {m.Title}";
    }
}
