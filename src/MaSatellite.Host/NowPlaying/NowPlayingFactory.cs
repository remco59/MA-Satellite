using MaSatellite.Host.Infrastructure;
using MaSatellite.Host.Library;
using Microsoft.Extensions.Logging;

namespace MaSatellite.Host.NowPlaying;

/// <summary>Builds the Now playing services with one shared, well-behaved HTTP client and an on-disk cache.</summary>
public static class NowPlayingFactory
{
    public static NowPlayingController Create(LibraryController library, AppPaths paths, string appVersion, ILoggerFactory loggers)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // MusicBrainz and LRCLIB ask API clients to identify themselves with a name, version and a way to reach the project.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MA-Satellite/{appVersion} (+https://github.com/remco59/MA-Satellite)");

        var cache = new JsonDiskCache(Path.Combine(paths.Root, "cache"));
        var musicBrainz = new MusicBrainzClient(http, cache, loggers.CreateLogger<MusicBrainzClient>());
        var lyrics = new LyricsService(http, cache, loggers.CreateLogger<LyricsService>());
        var about = new AboutService(musicBrainz, loggers.CreateLogger<AboutService>());
        return new NowPlayingController(library, lyrics, about, musicBrainz, http);
    }
}
