using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MaSatellite.Host.NowPlaying;

/// <summary>
/// A small file-per-key cache for metadata lookups (lyrics, MusicBrainz). Entries expire; a stored null is a
/// deliberate "nothing found" so repeated lookups do not hammer external services. Failures to read or write are
/// never fatal: the cache only ever saves requests.
/// </summary>
public sealed class JsonDiskCache
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly Func<DateTimeOffset> _clock;

    public JsonDiskCache(string root, Func<DateTimeOffset>? clock = null)
    {
        _root = root;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private sealed record Envelope<T>(DateTimeOffset Expires, T? Value);

    /// <summary>True when a live entry exists; <paramref name="value"/> is null for a cached "not found".</summary>
    public bool TryGet<T>(string ns, string key, out T? value)
    {
        value = default;
        try
        {
            var path = PathFor(ns, key);
            if (!File.Exists(path))
            {
                return false;
            }

            var envelope = JsonSerializer.Deserialize<Envelope<T>>(File.ReadAllText(path), Options);
            if (envelope is null || envelope.Expires <= _clock())
            {
                return false;
            }

            value = envelope.Value;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public void Set<T>(string ns, string key, T? value, TimeSpan ttl)
    {
        try
        {
            var path = PathFor(ns, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Envelope<T>(_clock() + ttl, value), Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written is just a cache miss next time.
        }
    }

    private string PathFor(string ns, string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(_root, ns, hash + ".json");
    }
}
