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
    private readonly IDisposable[] _owned;

    /// <param name="owned">Resources (HTTP client, rate limiter) that live and die with this controller.</param>
    public NowPlayingController(LibraryController library, LyricsService lyrics, AboutService about, params IDisposable[] owned)
    {
        _library = library;
        _lyrics = lyrics;
        _about = about;
        _owned = owned;
    }

    public LibraryController Library => _library;

    public void Dispose()
    {
        foreach (var resource in _owned)
        {
            resource.Dispose();
        }
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

    /// <summary>
    /// About information for the playing track. Without a usable Music Assistant record (for example playback that
    /// is not in a queue) the lookup still runs on title, artist and length alone and says so in its match note.
    /// </summary>
    public async Task<AboutInfo> GetAboutAsync(string? uri, string title, string artist, int? durationSeconds, CancellationToken ct = default)
    {
        MaItemDetails? track = null;
        if (!string.IsNullOrEmpty(uri))
        {
            try
            {
                track = await GetDetailsAsync(uri, ct).ConfigureAwait(false);
            }
            catch (MaException)
            {
                // Fall through to the bare track below.
            }
        }

        if (track is null)
        {
            track = new MaItemDetails(
                new MaItem(MaMediaType.Track, string.Empty, string.Empty, title, uri ?? string.Empty, artist, null, null, durationSeconds, null),
                string.IsNullOrWhiteSpace(artist) ? [] : [new MaArtistRef(artist, null, null, null, null)],
                null, null, null, null, null, null, null, null, null, null, null, null, [], null, []);
            return await _about.BuildAsync(track, [], ct).ConfigureAwait(false);
        }

        return await GetAboutAsync(track, ct).ConfigureAwait(false);
    }

    private async Task<AboutInfo> GetAboutAsync(MaItemDetails track, CancellationToken ct)
    {
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

    /// <summary>Adds or removes the track from the user's favourites and returns the new state.</summary>
    public async Task<bool> SetFavoriteAsync(string uri, bool favorite, CancellationToken ct = default)
    {
        var client = await _library.Service.GetClientAsync(ct).ConfigureAwait(false);
        if (favorite)
        {
            await client.AddFavoriteAsync(uri, ct).ConfigureAwait(false);
            _details.TryRemove(uri, out _);
            return true;
        }

        _details.TryRemove(uri, out _);
        var current = await client.GetDetailsAsync(uri, ct).ConfigureAwait(false);
        if (current.Item.Provider != "library" || string.IsNullOrEmpty(current.Item.ItemId))
        {
            throw new MaException("Music Assistant needs the library copy of this track to remove it from favourites.");
        }

        await client.RemoveFavoriteAsync(MaMediaType.Track, current.Item.ItemId, ct).ConfigureAwait(false);
        _details.TryRemove(uri, out _);
        return false;
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
