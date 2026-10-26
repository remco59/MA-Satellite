using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using MaSatellite.Core.Time;
using MaSatellite.Core.Tests.Support;
using MaSatellite.Core.Volume;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;

namespace MaSatellite.Integration.Tests;

/// <summary>Runs only when <c>MA_HOST</c> is set (and <c>MA_TOKEN</c> for the API part); otherwise the tests are skipped.</summary>
public sealed class RealMaFactAttribute : FactAttribute
{
    public RealMaFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MA_HOST")))
        {
            Skip = "Set MA_HOST (and MA_TOKEN) to run the real Music Assistant integration tests.";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MA_TOKEN")))
        {
            Skip = "Set MA_TOKEN to a long-lived Music Assistant token to run the integration tests.";
        }
    }
}

public class RealMusicAssistantTests
{
    private static string Host => Environment.GetEnvironmentVariable("MA_HOST")!;

    private static string Token => Environment.GetEnvironmentVariable("MA_TOKEN")!;

    private static int WebPort => int.TryParse(Environment.GetEnvironmentVariable("MA_WEB_PORT"), out var p) ? p : 8095;

    private static int SendspinPort => int.TryParse(Environment.GetEnvironmentVariable("MA_SENDSPIN_PORT"), out var p) ? p : 8927;

    private static async Task<T> EventuallyAsync<T>(Func<T?> probe, string what, int timeoutSeconds = 40)
        where T : class
    {
        var end = Environment.TickCount64 + (timeoutSeconds * 1000L);
        while (Environment.TickCount64 < end)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for: {what}");
    }

    [RealMaFact]
    public async Task Player_appears_plays_a_track_and_tracks_state()
    {
        var clock = StopwatchClock.Instance;
        using var output = new FakeAudioOutput(clock);
        var volume = new InMemoryVolumeControl();
        var clientId = Guid.NewGuid().ToString("D");
        var name = "ma-satellite integration " + clientId[..4];
        await using var service = new SendspinService(output, volume, clock);

        // We connect out to the server's Sendspin endpoint; no advertising or firewall needed for this test.
        await service.StartAsync(
            new SendspinOptions { ClientId = clientId, PlayerName = name },
            new ServerConnectionOptions { Mode = ConnectionMode.Connect, ServerHost = Host, ServerPort = SendspinPort });
        await EventuallyAsync(() => service.Status.Connection == ConnectionState.Connected ? service.Status : null, "connected to Music Assistant");

        await using var api = new MaClient(Host, WebPort, Token);
        await api.ConnectAsync();
        var player = await FindPlayerAsync(api, clientId, name);

        // pick a track: MA_TRACK_URI if given, otherwise the first library track
        var uri = Environment.GetEnvironmentVariable("MA_TRACK_URI");
        if (string.IsNullOrWhiteSpace(uri))
        {
            var tracks = await api.GetLibraryAsync(MaMediaType.Track, limit: 5);
            Assert.NotEmpty(tracks);
            uri = tracks[0].Uri;
        }

        await api.PlayMediaAsync(player.PlayerId, uri!, MaQueueOption.Replace);

        // state transitions: stream starts -> group playing -> metadata arrives -> audio flows and stays in sync
        await EventuallyAsync(() => service.Status.Session.StreamActive ? service.Status : null, "stream/start");
        await EventuallyAsync(() => service.Status.Session.Group.PlaybackState == GroupPlaybackState.Playing ? service.Status : null, "group playing");
        await EventuallyAsync(() => string.IsNullOrEmpty(service.Status.Session.Metadata.Title) ? null : service.Status, "metadata");
        await EventuallyAsync(() => output.FirstSoundMicros() is not null ? output : null, "audible audio");
        await EventuallyAsync(() => service.Status.InSync ? service.Status : null, "in sync", 20);
        Assert.True(service.Playback.BufferedMs > 0 || service.Playback.Playing);

        // pausing from MA ends the group's playing state
        await api.QueueCommandAsync(player.PlayerId, "pause");
        await EventuallyAsync(() => service.Status.Session.Group.PlaybackState == GroupPlaybackState.Stopped ? service.Status : null, "group stopped after pause");

        await api.QueueCommandAsync(player.PlayerId, "stop");
    }

    private static async Task<MaPlayer> FindPlayerAsync(MaClient api, string clientId, string name)
    {
        var end = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < end)
        {
            var found = MaClient.FindThisPlayer(await api.GetPlayersAsync(), clientId, name);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("The player never showed up in Music Assistant's player list.");
    }
}
