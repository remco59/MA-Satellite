using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using MaSatellite.Host.Presentation;

namespace MaSatellite.Host.Tests;

public class TransportLogicTests
{
    private static SessionState State(bool playing, params string[] commands) => new()
    {
        Group = new GroupState { PlaybackState = playing ? GroupPlaybackState.Playing : GroupPlaybackState.Stopped },
        Controller = new ControllerState { SupportedCommands = commands, SeekMaxMs = commands.Contains("seek") ? 200_000 : null },
    };

    [Fact]
    public void Play_pause_sends_pause_while_playing_and_play_otherwise()
    {
        Assert.Equal("pause", TransportLogic.PlayPause(State(true, "play", "pause"))!.Command);
        Assert.Equal("play", TransportLogic.PlayPause(State(false, "play", "pause"))!.Command);
    }

    [Fact]
    public void Play_pause_is_unavailable_when_the_server_does_not_offer_it()
    {
        Assert.Null(TransportLogic.PlayPause(State(true, "play")));
        Assert.Null(TransportLogic.PlayPause(State(false, "pause")));
        Assert.False(TransportLogic.Describe(State(true)).CanPlayPause);
    }

    [Fact]
    public void Next_previous_follow_support()
    {
        var s = State(true, "next");
        Assert.NotNull(TransportLogic.Next(s));
        Assert.Null(TransportLogic.Previous(s));
        var v = TransportLogic.Describe(s);
        Assert.True(v.CanNext);
        Assert.False(v.CanPrevious);
    }

    [Fact]
    public void Seek_is_clamped_and_requires_support()
    {
        var s = State(true, "seek");
        Assert.Equal(200_000, TransportLogic.Seek(s, 999_999)!.PositionMs);
        Assert.Equal(0, TransportLogic.Seek(s, -5)!.PositionMs);
        Assert.Null(TransportLogic.Seek(State(true, "play"), 1000));
        Assert.True(TransportLogic.Describe(s).CanSeek);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5_000, "0:05")]
    [InlineData(187_000, "3:07")]
    [InlineData(3_723_000, "1:02:03")]
    [InlineData(-10, "0:00")]
    public void Formats_time(long ms, string expected) => Assert.Equal(expected, ProgressFormatter.Time(ms));

    [Fact]
    public void Headline_tolerates_missing_fields()
    {
        Assert.Equal("A — T", ProgressFormatter.Headline(new MetadataState { Title = "T", Artist = "A" }));
        Assert.Equal("T", ProgressFormatter.Headline(new MetadataState { Title = "T" }));
        Assert.Equal(string.Empty, ProgressFormatter.Headline(new MetadataState { Artist = "A" }));
    }
}
