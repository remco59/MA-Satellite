namespace MaSatellite.Host.Presentation;

/// <summary>Tracks a playback request until the player reports the requested state, or a timeout.</summary>
public sealed class PlaybackFeedback
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(12);
    private readonly bool _wasPlaying;
    private readonly string _previousTitle;
    private readonly bool _waitForTrackChange;

    public PlaybackFeedback(string label, bool desiredPlaying, bool wasPlaying, string? previousTitle,
        bool waitForTrackChange = false, DateTimeOffset? startedAt = null)
    {
        Label = label;
        DesiredPlaying = desiredPlaying;
        _wasPlaying = wasPlaying;
        _previousTitle = previousTitle ?? string.Empty;
        _waitForTrackChange = waitForTrackChange;
        StartedAt = startedAt ?? DateTimeOffset.UtcNow;
    }

    public string Label { get; }
    public bool DesiredPlaying { get; }
    public DateTimeOffset StartedAt { get; }

    public bool IsConfirmed(bool playing, string? currentTitle)
    {
        if (playing != DesiredPlaying)
            return false;

        // For play-media while another song is playing, don't treat the old song as confirmation.
        return !_waitForTrackChange || !_wasPlaying ||
            !string.Equals(_previousTitle, currentTitle, StringComparison.OrdinalIgnoreCase);
    }

    public bool HasTimedOut(DateTimeOffset now) => now - StartedAt >= MaxWait;
}
