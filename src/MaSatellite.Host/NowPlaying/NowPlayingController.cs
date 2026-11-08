using System.Collections.Concurrent;
using MaSatellite.Host.Library;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.NowPlaying;

/// <summary>
/// Loads the secondary content of the Now playing page (lyrics, related music, About). Everything here is read-only,
/// runs off the playback path and is safe to cancel when the track changes; none of it can interrupt playback.
/// </summary>
public sealed class NowPlayingController : IDisposable
{
    private const int MaxCachedDetails = 64;
    private readonly LibraryController _library;
    private readonly LyricsService _lyrics;
    private readonly AboutService _about;
    private readonly ConcurrentDictionary<string, Task<MaItemDetails>> _details = new();

    public NowPlayingController(LibraryController library, LyricsService lyrics, AboutService about)
    {
        _library = library;
        _lyrics = lyrics;
        _about = about;
    }

    public LibraryController Library => _library;

    public void Dispose()
    {
    }

    /// <summary>Full Music Assistant record for an item, remembered for the session so tab switches cost nothing.</summary>
    public Task<MaItemDetails> GetDetailsAsync(string uri, CancellationToken ct = default)
    {
        if (_details.Count > MaxCachedDetails)
        {
            _details.Clear();
        }

        var task = _details.GetOrAdd(uri, async u =>
        {
            var client = await _library.Service.GetClientAsync(CancellationToken.None).ConfigureAwait(false);
            return await client.GetDetailsAsync(u, CancellationToken.None).ConfigureAwait(false);
        });
        if (task.IsFaulted)
        {
            _details.TryRemove(uri, out _);
        }

        return task.WaitAsync(ct);
    }

    public async Task<LyricsResult> GetLyricsAsync(LyricsQuery query, string? uri, CancellationToken ct = default)
    {
        MaItemDetails? details = null;
        if (!string.IsNullOrEmpty(uri))
        {
            try
            {
                details = await GetDetailsAsync(uri, ct).ConfigureAwait(false);
            }
            catch (MaException)
            {
                // Lyrics from the server are a bonus; fall back to LRCLIB.
            }
        }

        return await _lyrics.GetAsync(query, details, ct).ConfigureAwait(false);
    }

    public async Task<AboutInfo> GetAboutAsync(string uri, CancellationToken ct = default)
    {
        var track = await GetDetailsAsync(uri, ct).ConfigureAwait(false);
        var artists = new List<MaItemDetails>();
        foreach (var a in track.Artists.Where(a => !string.IsNullOrEmpty(a.Uri)).Take(3))
        {
            try
            {
                artists.Add(await GetDetailsAsync(a.Uri!, ct).ConfigureAwait(false));
            }
            catch (MaException)
            {
                // Biography is optional.
            }
        }

        return await _about.BuildAsync(track, artists, ct).ConfigureAwait(false);
    }

    /// <summary>Only what the server and its providers actually return; each section says why it is empty.</summary>
    public async Task<RelatedContent> GetRelatedAsync(string uri, CancellationToken ct = default)
    {
        var track = await GetDetailsAsync(uri, ct).ConfigureAwait(false);
        var client = await _library.Service.GetClientAsync(ct).ConfigureAwait(false);
        var artist = track.Artists.FirstOrDefault(a => !string.IsNullOrEmpty(a.ItemId) && !string.IsNullOrEmpty(a.Provider));
        var sections = new List<RelatedSection>();

        sections.Add(await SectionAsync("Similar tracks", "Music Assistant provider", async () =>
            (IReadOnlyList<MaItem>)(await client.GetSimilarTracksAsync(track.Item.ItemId, track.Item.Provider, 20, ct).ConfigureAwait(false))
                .Where(t => t.Uri != track.Item.Uri).ToList()).ConfigureAwait(false));

        if (artist is not null)
        {
            sections.Add(await SectionAsync("Similar artists", "Music Assistant provider", async () =>
                await client.GetSimilarArtistsAsync(artist.ItemId!, artist.Provider!, 16, ct).ConfigureAwait(false)).ConfigureAwait(false));
            sections.Add(await SectionAsync($"More from {artist.Name}", "Music Assistant", async () =>
                (IReadOnlyList<MaItem>)(await client.GetArtistTracksAsync(artist.ItemId!, artist.Provider!, ct).ConfigureAwait(false))
                    .Where(t => t.Uri != track.Item.Uri).Take(12).ToList()).ConfigureAwait(false));
            sections.Add(await SectionAsync($"Albums by {artist.Name}", "Music Assistant", async () =>
                (IReadOnlyList<MaItem>)(await client.GetChildrenAsync(new MaItem(MaMediaType.Artist, artist.ItemId!, artist.Provider!, artist.Name, artist.Uri ?? string.Empty, null, null, null, null, null), ct).ConfigureAwait(false))
                    .Take(12).ToList()).ConfigureAwait(false));
        }

        return new RelatedContent(sections);
    }

    private static async Task<RelatedSection> SectionAsync(string title, string source, Func<Task<IReadOnlyList<MaItem>>> load)
    {
        try
        {
            var items = await load().ConfigureAwait(false);
            return items.Count == 0
                ? new RelatedSection(title, source, RelatedStatus.Empty, [], "Nothing to suggest for this track.")
                : new RelatedSection(title, source, RelatedStatus.Ok, items);
        }
        catch (MaException ex)
        {
            return new RelatedSection(title, source, RelatedStatus.Unavailable, [], "Your Music Assistant providers do not offer this: " + ex.Message);
        }
    }
}
