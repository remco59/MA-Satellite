using System.Runtime.InteropServices.WindowsRuntime;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using MaSatellite.Host.Presentation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace MaSatellite.Windows.Media;

/// <summary>
/// Publishes now-playing info and enables the media keys, the volume-flyout media card and the lock screen controls
/// through System Media Transport Controls. A <see cref="MediaPlayer"/> that never plays is the supported way for a
/// desktop app to obtain SMTC; we only use it as the host of the transport controls.
/// </summary>
public sealed class SmtcController : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly SystemMediaTransportControls _smtc;
    private readonly ILogger _logger;
    private string _signature = string.Empty;
    private int _artworkVersion = -1;
    private bool _disposed;

    public SmtcController(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _player = new MediaPlayer();
        _player.CommandManager.IsEnabled = false; // we drive the controls ourselves
        _smtc = _player.SystemMediaTransportControls;
        _smtc.IsEnabled = false;
        _smtc.ButtonPressed += OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested += OnPositionChangeRequested;
    }

    /// <summary>A button or seek request from Windows, already translated to a Sendspin controller command.</summary>
    public event Action<ControllerCommand>? CommandRequested;

    /// <summary>Pushes the current session to Windows. <paramref name="serverNowMicros"/> extrapolates the playback position.</summary>
    public void Update(SessionState session, bool connected, long serverNowMicros)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var md = session.Metadata;
            var hasTrack = !string.IsNullOrEmpty(md.Title);
            if (!connected || (!hasTrack && !session.StreamActive))
            {
                if (_smtc.IsEnabled)
                {
                    _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                    _smtc.IsEnabled = false;
                    _signature = string.Empty;
                }

                return;
            }

            var view = TransportLogic.Describe(session);
            var signature = string.Join('|', md.Title, md.Artist, md.Album, md.Timestamp, md.Progress?.TrackDurationMs, md.Progress?.PlaybackSpeed, view.IsPlaying, view.CanPlayPause, view.CanNext, view.CanPrevious, view.CanSeek, session.Artwork?.Version);
            if (signature == _signature)
            {
                return;
            }

            _signature = signature;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = view.CanPlayPause && !view.IsPlaying;
            _smtc.IsPauseEnabled = view.CanPlayPause && view.IsPlaying;
            _smtc.IsNextEnabled = view.CanNext;
            _smtc.IsPreviousEnabled = view.CanPrevious;
            _smtc.IsStopEnabled = view.CanStop;
            _smtc.PlaybackStatus = view.IsPlaying ? MediaPlaybackStatus.Playing : hasTrack ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Stopped;

            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = md.Title ?? string.Empty;
            updater.MusicProperties.Artist = md.Artist ?? string.Empty;
            updater.MusicProperties.AlbumArtist = md.AlbumArtist ?? md.Artist ?? string.Empty;
            updater.MusicProperties.AlbumTitle = md.Album ?? string.Empty;
            if (md.Track is { } track and > 0)
            {
                updater.MusicProperties.TrackNumber = (uint)track;
            }

            if (session.Artwork is null)
            {
                updater.Thumbnail = null;
                _artworkVersion = -1;
            }
            else if (session.Artwork.Version != _artworkVersion)
            {
                _artworkVersion = session.Artwork.Version;
                _ = SetThumbnailAsync(session.Artwork);
            }

            updater.Update();

            if (md.Progress is { TrackDurationMs: > 0 } progress && serverNowMicros > 0)
            {
                var position = md.PositionMs(serverNowMicros) ?? 0;
                _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    MinSeekTime = TimeSpan.Zero,
                    EndTime = TimeSpan.FromMilliseconds(progress.TrackDurationMs),
                    MaxSeekTime = view.CanSeek ? TimeSpan.FromMilliseconds(progress.TrackDurationMs) : TimeSpan.Zero,
                    Position = TimeSpan.FromMilliseconds(position),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updating the media transport controls failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _smtc.ButtonPressed -= OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
        _smtc.IsEnabled = false;
        _player.Dispose();
    }

    internal static ControllerCommand? Map(SystemMediaTransportControlsButton button) => button switch
    {
        SystemMediaTransportControlsButton.Play => new ControllerCommand("play"),
        SystemMediaTransportControlsButton.Pause => new ControllerCommand("pause"),
        SystemMediaTransportControlsButton.Stop => new ControllerCommand("stop"),
        SystemMediaTransportControlsButton.Next => new ControllerCommand("next"),
        SystemMediaTransportControlsButton.Previous => new ControllerCommand("previous"),
        _ => null,
    };

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (Map(args.Button) is { } command)
        {
            CommandRequested?.Invoke(command);
        }
    }

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        CommandRequested?.Invoke(new ControllerCommand("seek", PositionMs: (long)args.RequestedPlaybackPosition.TotalMilliseconds));
    }

    private async Task SetThumbnailAsync(ArtworkImage artwork)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(artwork.Data.AsBuffer());
            stream.Seek(0);
            if (_disposed || artwork.Version != _artworkVersion)
            {
                stream.Dispose();
                return;
            }

            _smtc.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            _smtc.DisplayUpdater.Update();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not set the SMTC thumbnail");
        }
    }
}
