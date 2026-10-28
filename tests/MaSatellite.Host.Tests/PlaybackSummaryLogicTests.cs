using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using MaSatellite.Host.Library;
using MaSatellite.Host.Presentation;
using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Tests;

public class PlaybackSummaryLogicTests
{
    private static SatelliteStatus Local(string? title, string? artist, ConnectionState connection = ConnectionState.Connected) => new()
    {
        Connection = connection,
        Session = new SessionState
        {
            Metadata = new MetadataState { Title = title, Artist = artist },
            Group = new GroupState { PlaybackState = GroupPlaybackState.Playing },
            Artwork = new ArtworkImage([1, 2, 3], "jpeg", 7),
        },
    };

    private static QueueView Remote(MaQueueInfo? queue, params MaQueueItem[] items) =>
        new(new PlayTarget("living-room", "Living room", false, true, "play"), queue, items);

    private static MaQueueInfo Queue(int? currentIndex, MaQueueItem? currentItem = null) =>
        new("living-room", "Living room", true, true, 400, currentIndex, "play", false, "off", 12, currentItem);

    private static MaQueueItem Item(int index, string name) =>
        new($"item-{index}", name, "Artist", 190, index, "https://example.org/art.jpg", null);

    [Fact]
    public void Local_session_shows_current_track_even_without_queue_data()
    {
        var result = PlaybackSummaryLogic.FromLocal(Local("Time Will Tell", "Sean Rivers"), "This PC");

        Assert.Equal("Time Will Tell", result.Title);
        Assert.Equal("Sean Rivers", result.Artist);
        Assert.True(result.IsPlaying);
        Assert.True(result.HasTrack);
        Assert.Equal(7, result.LocalArtwork?.Version);
    }

    [Fact]
    public void Disconnected_local_session_does_not_show_stale_metadata()
    {
        var result = PlaybackSummaryLogic.FromLocal(Local("Old track", "Old artist", ConnectionState.Listening), "This PC");

        Assert.Equal("Nothing playing", result.Title);
        Assert.Equal("This PC", result.Artist);
        Assert.False(result.IsPlaying);
        Assert.Null(result.LocalArtwork);
    }

    [Fact]
    public void Queue_current_item_works_outside_fetched_first_page()
    {
        var result = PlaybackSummaryLogic.FromQueue(Remote(Queue(350, Item(350, "Beyond page")), Item(1, "First page")));

        Assert.Equal("Beyond page", result.Title);
        Assert.Equal("Artist", result.Artist);
        Assert.True(result.IsPlaying);
        Assert.Equal("https://example.org/art.jpg", result.ArtworkUrl);
    }

    [Fact]
    public void Queue_falls_back_to_index_when_current_item_missing()
    {
        var result = PlaybackSummaryLogic.FromQueue(Remote(Queue(3), Item(3, "Fallback track")));

        Assert.Equal("Fallback track", result.Title);
    }

    [Fact]
    public void Empty_queue_does_not_invent_playback()
    {
        var result = PlaybackSummaryLogic.FromQueue(Remote(null));

        Assert.Equal("Nothing playing", result.Title);
        Assert.False(result.HasTrack);
        Assert.False(result.IsPlaying);
    }
}
