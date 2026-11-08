using System.Text.Json;
using MaSatellite.MaApi.Models;

namespace MaSatellite.MaApi;

/// <summary>Turns Music Assistant's item JSON (full items and ItemMappings) into the small records the UI uses.</summary>
public static class MaItemParser
{
    /// <param name="baseUrl">Web address of the server, e.g. <c>http://192.168.0.98:8095</c>, used for the image proxy.</param>
    /// <param name="imageSize">Thumbnail size in pixels requested from the server's image proxy.</param>
    public static MaItem ParseItem(JsonElement e, string baseUrl, int imageSize = 256)
    {
        var type = ParseType(Str(e, "media_type"));
        var name = Str(e, "name") ?? string.Empty;
        var version = Str(e, "version");
        if (!string.IsNullOrEmpty(version) && type is MaMediaType.Track or MaMediaType.Album)
        {
            name = $"{name} ({version})";
        }

        string? subtitle = null;
        string? album = null;
        int? duration = null;
        if (e.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            subtitle = string.Join(", ", artists.EnumerateArray().Select(a => Str(a, "name")).Where(n => !string.IsNullOrEmpty(n)));
            if (subtitle.Length == 0)
            {
                subtitle = null;
            }
        }

        if (type == MaMediaType.Playlist)
        {
            subtitle = Str(e, "owner");
        }

        if (e.TryGetProperty("album", out var albumEl) && albumEl.ValueKind == JsonValueKind.Object)
        {
            album = Str(albumEl, "name");
        }

        if (e.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number && dur.TryGetInt32(out var d) && d > 0)
        {
            duration = d;
        }

        int? year = e.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yy) && yy > 0 ? yy : null;

        string? overview = Str(e, "description");
        IReadOnlyList<string>? genres = null;
        if (e.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            overview = Str(metadata, "description") ?? Str(metadata, "review")
                ?? Str(metadata, "biography") ?? overview;
            if (metadata.TryGetProperty("genres", out var genreArray) && genreArray.ValueKind == JsonValueKind.Array)
            {
                genres = genreArray.EnumerateArray()
                    .Where(g => g.ValueKind == JsonValueKind.String)
                    .Select(g => g.GetString()!)
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .ToList();
            }
        }

        return new MaItem(
            type,
            Str(e, "item_id") ?? string.Empty,
            Str(e, "provider") ?? string.Empty,
            name,
            Str(e, "uri") ?? string.Empty,
            subtitle,
            type == MaMediaType.Track ? album : null,
            type is MaMediaType.Album or MaMediaType.Track ? year : null,
            duration,
            ImageUrl(e, baseUrl, imageSize),
            !e.TryGetProperty("is_playable", out var p) || p.ValueKind != JsonValueKind.False,
            overview,
            genres);
    }

    public static IReadOnlyList<MaItem> ParseItems(JsonElement array, string baseUrl, int imageSize = 256)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => ParseItem(x, baseUrl, imageSize)).ToList();
    }

    public static MaPlayer ParsePlayer(JsonElement e) => new(
        Str(e, "player_id") ?? string.Empty,
        Str(e, "name") ?? string.Empty,
        Bool(e, "available") ?? true,
        Bool(e, "powered"),
        Str(e, "playback_state") ?? "idle",
        Str(e, "type") ?? "player",
        e.TryGetProperty("volume_level", out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : null);

    public static MaQueueInfo ParseQueue(JsonElement e, string baseUrl)
    {
        MaQueueItem? current = null;
        if (e.TryGetProperty("current_item", out var ci) && ci.ValueKind == JsonValueKind.Object)
        {
            current = ParseQueueItem(ci, baseUrl);
        }

        return new MaQueueInfo(
            Str(e, "queue_id") ?? string.Empty,
            Str(e, "display_name") ?? Str(e, "queue_id") ?? string.Empty,
            Bool(e, "active") ?? false,
            Bool(e, "available") ?? true,
            e.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Number ? items.GetInt32() : 0,
            e.TryGetProperty("current_index", out var idx) && idx.ValueKind == JsonValueKind.Number ? idx.GetInt32() : null,
            Str(e, "state") ?? "idle",
            Bool(e, "shuffle_enabled") ?? false,
            Str(e, "repeat_mode") ?? "off",
            e.TryGetProperty("elapsed_time", out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0,
            current);
    }

    public static MaQueueItem ParseQueueItem(JsonElement e, string baseUrl, int imageSize = 128)
    {
        string? subtitle = null;
        string? uri = null;
        string? artist = null;
        string? album = null;
        if (e.TryGetProperty("media_item", out var media) && media.ValueKind == JsonValueKind.Object)
        {
            var item = ParseItem(media, baseUrl, imageSize);
            subtitle = item.Description;
            uri = item.Uri;
            artist = item.Subtitle;
            album = item.AlbumName;
        }

        var imageUrl = ImageUrlFrom(e, "image", baseUrl, imageSize);
        if (imageUrl is null && e.TryGetProperty("media_item", out var mi) && mi.ValueKind == JsonValueKind.Object)
        {
            imageUrl = ImageUrl(mi, baseUrl, imageSize);
        }

        return new MaQueueItem(
            Str(e, "queue_item_id") ?? string.Empty,
            Str(e, "name") ?? string.Empty,
            subtitle,
            e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null,
            e.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0,
            imageUrl,
            uri,
            artist,
            album);
    }

    /// <summary>
    /// Parses a full item (from <c>music/item_by_uri</c>) into <see cref="MaItemDetails"/>. Provider payloads differ
    /// a lot, so every field is read defensively and left null when absent.
    /// </summary>
    public static MaItemDetails ParseDetails(JsonElement e, string baseUrl)
    {
        var item = ParseItem(e, baseUrl, imageSize: 512);
        var externalIds = ReadExternalIds(e);

        var artists = new List<MaArtistRef>();
        if (e.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
            {
                var name = Str(a, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var ids = ReadExternalIds(a);
                artists.Add(new MaArtistRef(name, Str(a, "item_id"), Str(a, "provider"), Str(a, "uri"),
                    Str(a, "mbid") ?? Str(a, "musicbrainz_id") ?? ids.GetValueOrDefault("musicbrainz_artistid")));
            }
        }

        string? albumName = null, albumUri = null, albumId = null, albumProvider = null, albumMbid = null;
        if (e.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumName = Str(album, "name");
            albumUri = Str(album, "uri");
            albumId = Str(album, "item_id");
            albumProvider = Str(album, "provider");
            albumMbid = Str(album, "mbid") ?? ReadExternalIds(album).GetValueOrDefault("musicbrainz_albumid");
        }

        string? plain = null, synced = null, label = null, copyright = null, release = null, description = item.Overview;
        var links = new List<MaLink>();
        if (e.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            plain = Str(meta, "lyrics");
            synced = Str(meta, "lrc_lyrics");
            label = Str(meta, "label");
            copyright = Str(meta, "copyright");
            release = Str(meta, "release_date");
            if (meta.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                foreach (var link in l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                {
                    if (Str(link, "url") is { Length: > 0 } url)
                    {
                        links.Add(new MaLink(Str(link, "type") ?? "website", url));
                    }
                }
            }
        }

        return new MaItemDetails(
            item,
            artists,
            albumName,
            albumUri,
            albumId,
            albumProvider,
            string.IsNullOrWhiteSpace(plain) ? null : plain,
            string.IsNullOrWhiteSpace(synced) ? null : synced,
            Str(e, "mbid") ?? Str(e, "musicbrainz_id") ?? externalIds.GetValueOrDefault(item.Type switch
            {
                MaMediaType.Track => "musicbrainz_recordingid",
                MaMediaType.Artist => "musicbrainz_artistid",
                MaMediaType.Album => "musicbrainz_albumid",
                _ => "musicbrainz_id",
            }),
            albumMbid,
            externalIds.GetValueOrDefault("isrc"),
            label,
            copyright,
            release,
            item.Genres ?? [],
            description,
            links);
    }

    /// <summary><c>external_ids</c> is a list of <c>[type, value]</c> pairs (or <c>{type, value}</c> objects) depending on the server version.</summary>
    private static Dictionary<string, string> ReadExternalIds(JsonElement e)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!e.TryGetProperty("external_ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in ids.EnumerateArray())
        {
            string? type = null, value = null;
            if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() >= 2)
            {
                type = entry[0].ValueKind == JsonValueKind.String ? entry[0].GetString() : null;
                value = entry[1].ValueKind == JsonValueKind.String ? entry[1].GetString() : null;
            }
            else if (entry.ValueKind == JsonValueKind.Object)
            {
                type = Str(entry, "type");
                value = Str(entry, "value");
            }

            if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(value))
            {
                result.TryAdd(type, value);
            }
        }

        return result;
    }

    /// <summary>Resolves an image reference to a URL the app can load, via the server's image proxy when needed.</summary>
    public static string? ImageUrlFromImage(JsonElement image, string baseUrl, int size)
    {
        if (image.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var path = Str(image, "path");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var remote = Bool(image, "remotely_accessible") ?? false;
        if (remote && size == 0)
        {
            return path;
        }

        if (remote)
        {
            return $"https://images.weserv.nl/?url={Uri.EscapeDataString(path)}&w={size}&h={size}&fit=cover&a=attention";
        }

        var proxyId = Str(image, "proxy_id");
        if (!string.IsNullOrEmpty(proxyId))
        {
            return $"{baseUrl.TrimEnd('/')}/imageproxy/{proxyId}?size={size}";
        }

        var encoded = Uri.EscapeDataString(Uri.EscapeDataString(path));
        return $"{baseUrl.TrimEnd('/')}/imageproxy?path={encoded}&provider={Uri.EscapeDataString(Str(image, "provider") ?? string.Empty)}&size={size}";
    }

    private static string? ImageUrl(JsonElement item, string baseUrl, int size)
    {
        // ItemMapping carries `image`; full items carry metadata.images (prefer thumb). Tracks fall back to their album.
        if (ImageUrlFrom(item, "image", baseUrl, size) is { } direct)
        {
            return direct;
        }

        if (item.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            var list = images.EnumerateArray().ToList();
            var pick = list.FirstOrDefault(i => Str(i, "type") == "thumb");
            if (pick.ValueKind != JsonValueKind.Object)
            {
                pick = list.FirstOrDefault();
            }

            if (pick.ValueKind == JsonValueKind.Object && ImageUrlFromImage(pick, baseUrl, size) is { } fromMeta)
            {
                return fromMeta;
            }
        }

        if (item.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            return ImageUrl(album, baseUrl, size);
        }

        return null;
    }

    private static string? ImageUrlFrom(JsonElement parent, string property, string baseUrl, int size) =>
        parent.TryGetProperty(property, out var img) && img.ValueKind == JsonValueKind.Object ? ImageUrlFromImage(img, baseUrl, size) : null;

    private static MaMediaType ParseType(string? value) => value switch
    {
        "artist" => MaMediaType.Artist,
        "album" => MaMediaType.Album,
        "track" => MaMediaType.Track,
        "playlist" => MaMediaType.Playlist,
        "radio" => MaMediaType.Radio,
        _ => MaMediaType.Other,
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
