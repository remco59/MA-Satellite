using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;

namespace MaSatellite.Host.Presentation;

/// <summary>Button states and commands for the mini-player and SMTC, derived from the server's controller state.</summary>
public sealed record TransportView(bool IsPlaying, bool CanPlayPause, bool CanPrevious, bool CanNext, bool CanSeek, bool CanStop, long? SeekMaxMs);

public static class TransportLogic
{
    public static TransportView Describe(SessionState s)
    {
        var c = s.Controller;
        var playing = s.Group.PlaybackState == GroupPlaybackState.Playing;
        var canPlayPause = playing ? c.Supports("pause") : c.Supports("play");
        return new TransportView(
            playing,
            canPlayPause,
            c.Supports("previous"),
            c.Supports("next"),
            c.Supports("seek") && c.SeekMaxMs is > 0,
            c.Supports("stop"),
            c.SeekMaxMs);
    }

    /// <summary>The command for the play/pause button: pause while playing, otherwise play.</summary>
    public static ControllerCommand? PlayPause(SessionState s)
    {
        var v = Describe(s);
        if (!v.CanPlayPause)
        {
            return null;
        }

        return new ControllerCommand(v.IsPlaying ? "pause" : "play");
    }

    public static ControllerCommand? Next(SessionState s) => s.Controller.Supports("next") ? new ControllerCommand("next") : null;

    public static ControllerCommand? Previous(SessionState s) => s.Controller.Supports("previous") ? new ControllerCommand("previous") : null;

    public static ControllerCommand? Seek(SessionState s, long positionMs)
    {
        if (!s.Controller.Supports("seek") || s.Controller.SeekMaxMs is not { } max)
        {
            return null;
        }

        return new ControllerCommand("seek", PositionMs: Math.Clamp(positionMs, 0, max));
    }
}
