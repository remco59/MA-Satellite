using MaSatellite.Host.Presentation;

namespace MaSatellite.Host.Tests;

public class PlaybackFeedbackTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Play_stays_pending_until_player_reports_playing()
    {
        var feedback = new PlaybackFeedback("Starting…", true, false, "Old", startedAt: Start);
        Assert.False(feedback.IsConfirmed(false, "New"));
        Assert.True(feedback.IsConfirmed(true, "New"));
    }

    [Fact]
    public void Replacing_a_playing_track_waits_for_changed_metadata()
    {
        var feedback = new PlaybackFeedback("Starting…", true, true, "Old", waitForTrackChange: true, startedAt: Start);
        Assert.False(feedback.IsConfirmed(true, "Old"));
        Assert.False(feedback.IsConfirmed(false, "New"));
        Assert.True(feedback.IsConfirmed(true, "New"));
    }

    [Fact]
    public void Pause_waits_for_stopped_playback()
    {
        var feedback = new PlaybackFeedback("Pausing…", false, true, "Track", startedAt: Start);
        Assert.False(feedback.IsConfirmed(true, "Track"));
        Assert.True(feedback.IsConfirmed(false, "Track"));
    }

    [Fact]
    public void Pending_feedback_expires_instead_of_spinning_forever()
    {
        var feedback = new PlaybackFeedback("Starting…", true, false, null, startedAt: Start);
        Assert.False(feedback.HasTimedOut(Start.AddSeconds(11)));
        Assert.True(feedback.HasTimedOut(Start.AddSeconds(12)));
    }
}
