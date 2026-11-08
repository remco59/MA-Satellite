using System.Globalization;
using System.Text.RegularExpressions;
using MaSatellite.MaApi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Host.NowPlaying;

/// <summary>
/// Builds the About tab from what the library already knows plus MusicBrainz. Facts keep their provenance: structured
/// MusicBrainz data is marked verified only when it is tied to this recording by id or by a confident match; written
/// descriptions are always marked community text. Failures in one section never hide the others.
/// </summary>
public sealed partial class AboutService
{
    private const string LibrarySource = "Music Assistant library";
    private const string MbSource = "MusicBrainz";
    private const int MaxBiographyChars = 700;

    private readonly MusicBrainzClient _musicBrainz;
    private readonly ILogger _logger;

    public AboutService(MusicBrainzClient musicBrainz, ILogger? logger = null)
    {
        _musicBrainz = musicBrainz;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <param name="artistDetails">Music Assistant's full records for the credited artists (biographies live there); may be empty.</param>
    public async Task<AboutInfo> BuildAsync(MaItemDetails track, IReadOnlyList<MaItemDetails> artistDetails, CancellationToken ct = default)
    {
        var facts = new List<AboutFact>();
        var tags = new List<string>(track.Genres);
        var warnings = new List<string>();
        string? note = null;
        string? artwork = null;

        AddLibraryFacts(track, facts);

        var key = new TrackKey(track.Item.Name, track.Artists.Select(a => a.Name).ToList(), track.Item.DurationSeconds);
        MbRecording? recording = null;
        try
        {
            if (!string.IsNullOrEmpty(track.MusicBrainzId))
            {
                recording = await _musicBrainz.GetRecordingAsync(track.MusicBrainzId, ct).ConfigureAwait(false);
                note = recording is null ? null : "Identified through the MusicBrainz id stored in your library.";
            }

            if (recording is null)
            {
                recording = await _musicBrainz.FindRecordingAsync(key, ct).ConfigureAwait(false);
                note = recording is null
                    ? "No confident MusicBrainz match was found, so only library information is shown."
                    : $"Matched to MusicBrainz by title, artist and length ({recording.Confidence:P0} confidence).";
            }

            if (recording is not null)
            {
                (artwork) = await AddRecordingFactsAsync(track, recording, facts, tags, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "MusicBrainz lookup failed");
            warnings.Add("MusicBrainz could not be reached, so only library information is shown.");
        }

        var artists = await BuildArtistsAsync(track, recording, artistDetails, warnings, ct).ConfigureAwait(false);
        tags.AddRange(artists.SelectMany(a => a.Genres));
        return new AboutInfo(
            Dedupe(facts),
            artists,
            tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(Title).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
            artwork,
            note,
            warnings);
    }

    private static void AddLibraryFacts(MaItemDetails t, List<AboutFact> facts)
    {
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add(new AboutFact(label, value, InfoTrust.Library, LibrarySource));
            }
        }

        Add("Album", t.AlbumName);
        Add("Release date", t.ReleaseDate);
        if (t.ReleaseDate is null && t.Item.Year is { } y)
        {
            Add("Year", y.ToString(CultureInfo.InvariantCulture));
        }

        Add("Label", t.Label);
        if (t.Item.DurationSeconds is { } d)
        {
            Add("Duration", $"{d / 60}:{d % 60:00}");
        }

        Add("ISRC", t.Isrc);
        Add("Copyright", t.Copyright);
        if (t.Genres.Count > 0)
        {
            Add("Genre", string.Join(" · ", t.Genres.Take(4)));
        }
    }

    private async Task<string?> AddRecordingFactsAsync(MaItemDetails track, MbRecording rec, List<AboutFact> facts, List<string> tags, CancellationToken ct)
    {
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add(new AboutFact(label, value, InfoTrust.Verified, MbSource));
            }
        }

        Add("First released", rec.FirstReleaseDate);
        tags.AddRange(rec.Genres.Take(5));

        var chosen = ChooseRelease(rec, track.AlbumName);
        if (chosen is null)
        {
            return null;
        }

        Add("Release", chosen.Title);
        Add("Release type", chosen.PrimaryType);
        Add("Country", chosen.Country);
        var release = await _musicBrainz.GetReleaseAsync(chosen.Id, ct).ConfigureAwait(false);
        Add("Record label", release?.Label);
        Add("Catalogue no.", release?.CatalogNumber);
        return MusicBrainzClient.CoverArtUrl(chosen.Id);
    }

    /// <summary>Prefers the release that matches the library album, otherwise the earliest official one.</summary>
    internal static MbRelease? ChooseRelease(MbRecording rec, string? libraryAlbum)
    {
        if (!string.IsNullOrWhiteSpace(libraryAlbum))
        {
            var same = rec.Releases
                .Where(r => TrackMatcher.Similarity(r.Title, libraryAlbum) >= 0.9)
                .OrderBy(r => r.Status == "Official" ? 0 : 1)
                .ThenBy(r => r.Date ?? "9999")
                .FirstOrDefault();
            if (same is not null)
            {
                return same;
            }
        }

        return rec.Releases
            .OrderBy(r => r.Status == "Official" ? 0 : 1)
            .ThenBy(r => string.IsNullOrEmpty(r.Date) ? "9999" : r.Date)
            .FirstOrDefault();
    }

    private async Task<IReadOnlyList<ArtistAbout>> BuildArtistsAsync(MaItemDetails track, MbRecording? recording, IReadOnlyList<MaItemDetails> maArtists, List<string> warnings, CancellationToken ct)
    {
        var result = new List<ArtistAbout>();
        foreach (var credited in track.Artists.Take(3))
        {
            var ma = maArtists.FirstOrDefault(a => TrackMatcher.Normalize(a.Item.Name) == TrackMatcher.Normalize(credited.Name));
            var facts = new List<AboutFact>();
            var links = new List<MbLink>();
            var genres = new List<string>(ma?.Genres ?? []);

            try
            {
                var mbid = credited.MusicBrainzId
                    ?? recording?.Artists.FirstOrDefault(a => TrackMatcher.Normalize(a.Name) == TrackMatcher.Normalize(credited.Name))?.Id
                    ?? await _musicBrainz.FindArtistIdAsync(credited.Name, ct).ConfigureAwait(false);
                if (mbid is not null && await _musicBrainz.GetArtistAsync(mbid, ct).ConfigureAwait(false) is { } mb)
                {
                    void Add(string label, string? value)
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            facts.Add(new AboutFact(label, value, InfoTrust.Verified, MbSource));
                        }
                    }

                    Add("Type", mb.Type);
                    Add("Country", mb.Country);
                    Add("Active", ActiveYears(mb));
                    Add("Note", mb.Disambiguation);
                    genres.AddRange(mb.Genres);
                    links.AddRange(mb.Links);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "MusicBrainz artist lookup failed");
                warnings.Add($"Details about {credited.Name} could not be loaded from MusicBrainz.");
            }

            links.AddRange((ma?.Links ?? []).Select(l => new MbLink(l.Type, l.Url)));
            var bio = Concise(ma?.Description);
            result.Add(new ArtistAbout(
                credited.Name,
                bio,
                bio is null ? null : "Written by a community or provider, via Music Assistant",
                facts,
                genres.Select(Title).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList(),
                links.DistinctBy(l => l.Url).Take(5).ToList(),
                credited.Uri));
        }

        return result;
    }

    private static string? ActiveYears(MbArtist a)
    {
        var begin = a.Begin?.Split('-')[0];
        if (string.IsNullOrEmpty(begin))
        {
            return null;
        }

        var end = a.Ended ? a.End?.Split('-')[0] : "present";
        return string.IsNullOrEmpty(end) ? begin : $"{begin} – {end}";
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    /// <summary>A short, plain-text excerpt of a description: tags stripped, cut at a sentence end. Never the whole article.</summary>
    public static string? Concise(string? text, int max = MaxBiographyChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var plain = System.Net.WebUtility.HtmlDecode(HtmlTag().Replace(text, " "));
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        if (plain.Length <= max)
        {
            return plain;
        }

        var cut = plain[..max];
        var sentenceEnd = cut.LastIndexOfAny(['.', '!', '?']);
        return sentenceEnd > max / 2 ? cut[..(sentenceEnd + 1)] : cut.TrimEnd() + "…";
    }

    private static List<AboutFact> Dedupe(List<AboutFact> facts)
    {
        // The same label from a verified source replaces the library's copy of it.
        var verified = facts.Where(f => f.Trust == InfoTrust.Verified).Select(f => f.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return facts.Where(f => f.Trust == InfoTrust.Verified || !verified.Contains(f.Label)).ToList();
    }

    private static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
