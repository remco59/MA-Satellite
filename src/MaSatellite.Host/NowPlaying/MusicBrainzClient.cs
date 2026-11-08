using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Host.NowPlaying;

public sealed record MbRelease(string Id, string Title, string? Date, string? Status, string? Country, string? PrimaryType, string? Label, string? CatalogNumber);

public sealed record MbArtistCredit(string Name, string? Id);

public sealed record MbRecording(
    string Id,
    string Title,
    IReadOnlyList<MbArtistCredit> Artists,
    int? LengthSeconds,
    string? FirstReleaseDate,
    IReadOnlyList<string> Genres,
    IReadOnlyList<MbRelease> Releases,
    double Confidence);

public sealed record MbArtist(string Id, string Name, string? Type, string? Country, string? Begin, string? End, bool Ended, string? Disambiguation, IReadOnlyList<string> Genres, IReadOnlyList<MbLink> Links);

public sealed record MbLink(string Type, string Url);

/// <summary>
/// Read-only MusicBrainz access (open data, no key). It identifies itself with a User-Agent and keeps to the
/// documented limit of one request per second. Lookups by MBID are exact; searches only return a recording when
/// <see cref="TrackMatcher"/> is confident, so a wrong match is reported as "no match" instead.
/// </summary>
public sealed class MusicBrainzClient : IDisposable
{
    public const string ApiBase = "https://musicbrainz.org/ws/2";
    private static readonly TimeSpan FoundTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissingTtl = TimeSpan.FromDays(2);

    private readonly HttpClient _http;
    private readonly JsonDiskCache _cache;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minInterval;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public MusicBrainzClient(HttpClient http, JsonDiskCache cache, ILogger? logger = null, TimeSpan? minInterval = null)
    {
        _http = http;
        _cache = cache;
        _logger = logger ?? NullLogger.Instance;
        _minInterval = minInterval ?? TimeSpan.FromMilliseconds(1100);
    }

    public void Dispose() => _gate.Dispose();

    public async Task<MbRecording?> GetRecordingAsync(string mbid, CancellationToken ct = default)
    {
        if (!Guid.TryParse(mbid, out var id))
        {
            return null;
        }

        return await CachedAsync("mb-recording", id.ToString(), async () =>
        {
            var json = await GetJsonAsync($"{ApiBase}/recording/{id}?inc=artist-credits+releases+genres+isrcs&fmt=json", ct).ConfigureAwait(false);
            return json is null ? null : ParseRecording(json.Value, 1.0);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Finds the recording for a track without an id; null unless title, artist and duration agree.</summary>
    public async Task<MbRecording?> FindRecordingAsync(TrackKey track, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track.Title) || track.Artists.Count == 0)
        {
            return null;
        }

        var key = $"{TrackMatcher.Normalize(track.Title)}|{TrackMatcher.Normalize(string.Join(' ', track.Artists))}|{track.DurationSeconds / 4}";
        return await CachedAsync("mb-find", key, async () =>
        {
            var query = $"recording:\"{Escape(track.Title)}\" AND artist:\"{Escape(track.Artists[0])}\"";
            var json = await GetJsonAsync($"{ApiBase}/recording?query={Uri.EscapeDataString(query)}&limit=8&fmt=json", ct).ConfigureAwait(false);
            if (json is null || !json.Value.TryGetProperty("recordings", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            MbRecording? best = null;
            foreach (var candidate in list.EnumerateArray())
            {
                var parsed = ParseRecording(candidate, 0);
                var score = TrackMatcher.Score(track, parsed.Title, parsed.Artists.Select(a => a.Name).ToList(), parsed.LengthSeconds);
                if (TrackMatcher.IsConfident(score) && (best is null || score > best.Confidence))
                {
                    best = parsed with { Confidence = score };
                }
            }

            return best is null ? null : await GetRecordingAsync(best.Id, ct).ConfigureAwait(false) is { } full ? full with { Confidence = best.Confidence } : best;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Label and catalogue number for one release (a separate lookup: recordings do not carry them).</summary>
    public async Task<MbRelease?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(releaseId, out var id))
        {
            return null;
        }

        return await CachedAsync("mb-release", id.ToString(), async () =>
        {
            var json = await GetJsonAsync($"{ApiBase}/release/{id}?inc=labels+release-groups&fmt=json", ct).ConfigureAwait(false);
            return json is null ? null : ParseRelease(json.Value);
        }, ct).ConfigureAwait(false);
    }

    public async Task<MbArtist?> GetArtistAsync(string mbid, CancellationToken ct = default)
    {
        if (!Guid.TryParse(mbid, out var id))
        {
            return null;
        }

        return await CachedAsync("mb-artist", id.ToString(), async () =>
        {
            var json = await GetJsonAsync($"{ApiBase}/artist/{id}?inc=genres+url-rels&fmt=json", ct).ConfigureAwait(false);
            return json is null ? null : ParseArtist(json.Value);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves an artist by name only when MusicBrainz returns exactly one strongly matching artist.</summary>
    public async Task<string?> FindArtistIdAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return await CachedAsync("mb-artist-find", TrackMatcher.Normalize(name), async () =>
        {
            var json = await GetJsonAsync($"{ApiBase}/artist?query={Uri.EscapeDataString($"artist:\"{Escape(name)}\"")}&limit=5&fmt=json", ct).ConfigureAwait(false);
            if (json is null || !json.Value.TryGetProperty("artists", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var strong = list.EnumerateArray()
                .Where(a => a.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt32() >= 95
                    && TrackMatcher.Normalize(Str(a, "name")) == TrackMatcher.Normalize(name))
                .Select(a => Str(a, "id"))
                .Where(i => i is not null)
                .ToList();
            // Two different artists with the same name: refuse to guess.
            return strong.Count == 1 ? strong[0] : null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Cover Art Archive front cover for a release (a redirecting image URL; may 404 when no art exists).</summary>
    public static string CoverArtUrl(string releaseId, int size = 500) => $"https://coverartarchive.org/release/{releaseId}/front-{size}";

    private async Task<T?> CachedAsync<T>(string ns, string key, Func<Task<T?>> load, CancellationToken ct) where T : class
    {
        if (_cache.TryGet<T>(ns, key, out var hit))
        {
            return hit;
        }

        var value = await load().ConfigureAwait(false);
        _cache.Set(ns, key, value, value is null ? MissingTtl : FoundTtl);
        ct.ThrowIfCancellationRequested();
        return value;
    }

    /// <summary>Null on 404; throws on transport errors and other statuses so they are not cached as "missing".</summary>
    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastRequest + _minInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                return doc.RootElement.Clone();
            }
            finally
            {
                _lastRequest = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static MbRecording ParseRecording(JsonElement e, double confidence)
    {
        var artists = new List<MbArtistCredit>();
        if (e.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in credits.EnumerateArray())
            {
                var name = Str(c, "name") ?? (c.TryGetProperty("artist", out var a0) ? Str(a0, "name") : null);
                if (name is not null)
                {
                    artists.Add(new MbArtistCredit(name, c.TryGetProperty("artist", out var a) ? Str(a, "id") : null));
                }
            }
        }

        var releases = new List<MbRelease>();
        if (e.TryGetProperty("releases", out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            releases.AddRange(rels.EnumerateArray().Select(ParseRelease));
        }

        var genres = new List<string>();
        if (e.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            genres.AddRange(g.EnumerateArray().OrderByDescending(x => x.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0)
                .Select(x => Str(x, "name")).Where(n => !string.IsNullOrWhiteSpace(n))!);
        }

        int? length = e.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number ? (int)Math.Round(l.GetInt32() / 1000.0) : null;
        return new MbRecording(Str(e, "id") ?? string.Empty, Str(e, "title") ?? string.Empty, artists, length, Str(e, "first-release-date"), genres, releases, confidence);
    }

    internal static MbRelease ParseRelease(JsonElement r)
    {
        string? label = null, catalog = null, primary = null;
        if (r.TryGetProperty("label-info", out var info) && info.ValueKind == JsonValueKind.Array)
        {
            foreach (var li in info.EnumerateArray())
            {
                label ??= li.TryGetProperty("label", out var lab) && lab.ValueKind == JsonValueKind.Object ? Str(lab, "name") : null;
                catalog ??= Str(li, "catalog-number");
            }
        }

        if (r.TryGetProperty("release-group", out var rg) && rg.ValueKind == JsonValueKind.Object)
        {
            primary = Str(rg, "primary-type");
        }

        return new MbRelease(Str(r, "id") ?? string.Empty, Str(r, "title") ?? string.Empty, Str(r, "date"), Str(r, "status"), Str(r, "country"), primary, label, catalog);
    }

    internal static MbArtist ParseArtist(JsonElement a)
    {
        var span = a.TryGetProperty("life-span", out var ls) && ls.ValueKind == JsonValueKind.Object ? ls : default;
        string? country = Str(a, "country");
        if (country is null && a.TryGetProperty("area", out var area) && area.ValueKind == JsonValueKind.Object)
        {
            country = Str(area, "name");
        }

        var genres = a.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().OrderByDescending(x => x.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0)
                .Select(x => Str(x, "name")).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Take(8).ToList()
            : [];

        var links = new List<MbLink>();
        if (a.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            foreach (var rel in rels.EnumerateArray())
            {
                if (rel.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.Object && Str(url, "resource") is { } resource
                    && Str(rel, "type") is "official homepage" or "wikipedia" or "wikidata" or "discogs" or "bandcamp")
                {
                    links.Add(new MbLink(Str(rel, "type")!, resource));
                }
            }
        }

        return new MbArtist(
            Str(a, "id") ?? string.Empty,
            Str(a, "name") ?? string.Empty,
            Str(a, "type"),
            country,
            span.ValueKind == JsonValueKind.Object ? Str(span, "begin") : null,
            span.ValueKind == JsonValueKind.Object ? Str(span, "end") : null,
            span.ValueKind == JsonValueKind.Object && span.TryGetProperty("ended", out var ended) && ended.ValueKind == JsonValueKind.True,
            Str(a, "disambiguation"),
            genres,
            links);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
