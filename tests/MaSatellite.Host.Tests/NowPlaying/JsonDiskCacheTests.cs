using MaSatellite.Host.NowPlaying;

namespace MaSatellite.Host.Tests.NowPlaying;

public class JsonDiskCacheTests
{
    private sealed record Payload(string Name, int N);

    [Fact]
    public void Stores_values_until_they_expire_and_remembers_misses()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var cache = new JsonDiskCache(dir.Path, () => now);

        Assert.False(cache.TryGet<Payload>("ns", "k", out _));
        cache.Set("ns", "k", new Payload("a", 1), TimeSpan.FromHours(1));
        cache.Set<Payload>("ns", "missing", null, TimeSpan.FromHours(1));

        Assert.True(cache.TryGet<Payload>("ns", "k", out var hit));
        Assert.Equal(new Payload("a", 1), hit);
        Assert.True(cache.TryGet<Payload>("ns", "missing", out var miss));
        Assert.Null(miss);

        now = now.AddHours(2);
        Assert.False(cache.TryGet<Payload>("ns", "k", out _));
    }

    [Fact]
    public void A_corrupt_cache_file_is_a_miss_not_an_error()
    {
        using var dir = new TempDir();
        var cache = new JsonDiskCache(dir.Path);
        cache.Set("ns", "k", new Payload("a", 1), TimeSpan.FromHours(1));
        foreach (var f in Directory.GetFiles(dir.Path, "*.json", SearchOption.AllDirectories))
        {
            File.WriteAllText(f, "{not json");
        }

        Assert.False(cache.TryGet<Payload>("ns", "k", out _));
    }
}
