using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaSatellite.MaApi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Host.NowPlaying;

public enum LyricsKind
{
    /// <summary>No lyrics exist (or were found) for this track.</summary>
    None,
    Plain,
    Synced,
    Instrumental,

    /// <summary>A provider could not be reached; worth retrying, never cached.</summary>
    Error,
}

public sealed record LyricsResult(LyricsKind Kind, IReadOnlyList<LyricLine> Lines, string? PlainText, string? Source, string? Message = null)
{
    public static LyricsResult None { get; } = new(LyricsKind.None, [], null, null);
}

public sealed record LyricsQuery(string Title, string Artist, string? Album, int? DurationSeconds);

/// <summary>
/// Lyrics from Music Assistant first (it may already hold synced lyrics from its own provider), then from
/// LRCLIB, an open, key-less lyrics database that publishes an API for exactly this use. Nothing is scraped.
/// </summary>
public sealed class LyricsService
{
    public const string LrcLibBase = "https://lrclib.net/api";
    private static readonly TimeSpan FoundTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissingTtl = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly JsonDiskCache _cache;
    private readonly ILogger _logger;

    public LyricsService(HttpClient http, JsonDiskCache cache, ILogger? logger = null)
    {
        _http = http;
        _cache = cache;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<LyricsResult> GetAsync(LyricsQuery query, MaItemDetails? fromMusicAssistant, CancellationToken ct = default)
    {
        if (FromMusicAssistant(fromMusicAssistant) is { } local)
        {
            return local;
        }

        var key = $"{TrackMatcher.Normalize(query.Artist)}|{TrackMatcher.Normalize(query.Title)}|{TrackMatcher.Normalize(query.Album)}|{query.DurationSeconds / 3}";
        if (_cache.TryGet<LyricsResult>("lyrics", key, out var cached))
        {
            return cached ?? LyricsResult.None;
        }

        try
        {
            var result = await LookUpAsync(query, ct).ConfigureAwait(false);
            _cache.Set("lyrics", key, result.Kind == LyricsKind.None ? null : result, result.Kind == LyricsKind.None ? MissingTtl : FoundTtl);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "LRCLIB lookup failed");
            return new LyricsResult(LyricsKind.Error, [], null, "LRCLIB", "Lyrics could not be loaded right now.");
        }
    }

    internal static LyricsResult? FromMusicAssistant(MaItemDetails? details)
    {
        if (details is null)
        {
            return null;
        }

        const string Source = "Music Assistant";
        if (LrcParser.LooksSynced(details.SyncedLyrics))
        {
            return new LyricsResult(LyricsKind.Synced, LrcParser.Parse(details.SyncedLyrics), LrcParser.ToPlainText(details.SyncedLyrics!), Source);
        }

        if (LrcParser.LooksSynced(details.PlainLyrics))
        {
            return new LyricsResult(LyricsKind.Synced, LrcParser.Parse(details.PlainLyrics), LrcParser.ToPlainText(details.PlainLyrics!), Source);
        }

        return details.PlainLyrics is { } plain ? new LyricsResult(LyricsKind.Plain, [], plain.Trim(), Source) : null;
    }

    private async Task<LyricsResult> LookUpAsync(LyricsQuery q, CancellationToken ct)
    {
        var wanted = new TrackKey(q.Title, [q.Artist], q.DurationSeconds);

        // Exact signature lookup first: artist + title + album + duration identifies one recording.
        var url = $"{LrcLibBase}/get?track_name={Uri.EscapeDataString(q.Title)}&artist_name={Uri.EscapeDataString(q.Artist)}"
            + (string.IsNullOrWhiteSpace(q.Album) ? string.Empty : $"&album_name={Uri.EscapeDataString(q.Album)}")
            + (q.DurationSeconds is { } d ? $"&duration={d}" : string.Empty);
        using (var response = await _http.GetAsync(url, ct).ConfigureAwait(false))
        {
            if (response.IsSuccessStatusCode)
            {
                var hit = await response.Content.ReadFromJsonAsync<LrcLibRecord>(cancellationToken: ct).ConfigureAwait(false);
                if (hit is not null && Accept(wanted, hit))
                {
                    return ToResult(hit);
                }
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                throw new HttpRequestException($"LRCLIB answered {(int)response.StatusCode}.");
            }
        }

        var searchUrl = $"{LrcLibBase}/search?track_name={Uri.EscapeDataString(q.Title)}&artist_name={Uri.EscapeDataString(q.Artist)}";
        using var searchResponse = await _http.GetAsync(searchUrl, ct).ConfigureAwait(false);
        if (searchResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return LyricsResult.None;
        }

        searchResponse.EnsureSuccessStatusCode();
        var results = await searchResponse.Content.ReadFromJsonAsync<List<LrcLibRecord>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        var best = results
            .Where(r => Accept(wanted, r))
            .OrderByDescending(r => TrackMatcher.Score(wanted, r.TrackName ?? string.Empty, [r.ArtistName ?? string.Empty], r.Duration is { } dd ? (int)Math.Round(dd) : null))
            .ThenByDescending(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
            .FirstOrDefault();
        return best is null ? LyricsResult.None : ToResult(best);
    }

    private static bool Accept(TrackKey wanted, LrcLibRecord r) =>
        TrackMatcher.IsConfident(TrackMatcher.Score(wanted, r.TrackName ?? string.Empty, [r.ArtistName ?? string.Empty], r.Duration is { } d ? (int)Math.Round(d) : null));

    private static LyricsResult ToResult(LrcLibRecord r)
    {
        const string Source = "LRCLIB";
        if (r.Instrumental == true)
        {
            return new LyricsResult(LyricsKind.Instrumental, [], null, Source);
        }

        if (LrcParser.LooksSynced(r.SyncedLyrics))
        {
            return new LyricsResult(LyricsKind.Synced, LrcParser.Parse(r.SyncedLyrics), r.PlainLyrics ?? LrcParser.ToPlainText(r.SyncedLyrics!), Source);
        }

        return string.IsNullOrWhiteSpace(r.PlainLyrics)
            ? LyricsResult.None
            : new LyricsResult(LyricsKind.Plain, [], r.PlainLyrics.Trim(), Source);
    }

    private sealed class LrcLibRecord
    {
        [JsonPropertyName("trackName")]
        public string? TrackName { get; set; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("duration")]
        public double? Duration { get; set; }

        [JsonPropertyName("instrumental")]
        public bool? Instrumental { get; set; }

        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }

        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }
    }
}
