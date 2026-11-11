using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using MaSatellite.Host.Library;
using MaSatellite.Host.Orchestration;
using MaSatellite.Host.Presentation;
using MaSatellite.MaApi;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace MaSatellite.App.Views;

/// <summary>The tray flyout: a compact now playing view and queue, with transport, seek bar and volume.</summary>
internal sealed partial class MiniPlayerWindow : Window
{
    private const int BaseWidth = 340;
    private const int BaseHeight = 382;

    private readonly SatelliteHost _host;
    private readonly Func<LibraryController?> _library;
    private readonly Func<Rectangle?> _trayRect;
    private readonly Action _openFullApp;
    private readonly DispatcherQueueTimer _progressTimer;
    private readonly DispatcherQueueTimer _seekDebounce;
    private bool _updating;
    private int _artworkVersion = -1;
    private long _hiddenAtTicks;
    private long _pendingSeekMs;
    private PlaybackFeedback? _pendingPlayback;
    private bool _showingQueue;
    private PlayTarget? _queueTarget;
    private string? _queueTrackKey;
    private int _queueLoadId;

    public MiniPlayerWindow(SatelliteHost host, Func<LibraryController?> library, Func<Rectangle?> trayRect, Action openFullApp)
    {
        _host = host;
        _library = library;
        _trayRect = trayRect;
        _openFullApp = openFullApp;
        InitializeComponent();
        Title = "ma-satellite";
        AuroraWindowChrome.SetIcon(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
        }

        AppWindow.IsShownInSwitchers = false;
        Activated += OnActivated;

        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
        _progressTimer.Tick += (_, _) => Refresh();

        _seekDebounce = DispatcherQueue.CreateTimer();
        _seekDebounce.Interval = TimeSpan.FromMilliseconds(300);
        _seekDebounce.IsRepeating = false;
        _seekDebounce.Tick += (_, _) => SendSeek();

        _host.Volume.Changed += OnSystemVolumeChanged;
        Closed += (_, _) =>
        {
            _progressTimer.Stop();
            _host.Volume.Changed -= OnSystemVolumeChanged;
        };
    }

    public bool IsShown { get; private set; }

    /// <summary>Hover previews do not take focus and may close when the cursor moves away.</summary>
    public bool IsHoverPreview { get; private set; }

    public bool ContainsScreenPoint(Point point)
    {
        if (!IsShown)
        {
            return false;
        }

        // AppWindow and Shell_NotifyIconGetRect use physical screen pixels (including on scaled displays).
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        return point.X >= position.X && point.X < position.X + size.Width
            && point.Y >= position.Y && point.Y < position.Y + size.Height;
    }

    /// <summary>A click on a hover preview keeps it open for interactive controls.</summary>
    public void PinFlyout()
    {
        if (!IsShown)
        {
            return;
        }

        IsHoverPreview = false;
        Activate();
    }

    /// <summary>Shows the flyout, or hides it when it is already open. Ignores the click that merely dismissed it.</summary>
    public void Toggle()
    {
        if (IsShown)
        {
            HideFlyout();
        }
        else if (Environment.TickCount64 - _hiddenAtTicks > 300)
        {
            ShowFlyout();
        }
    }

    public void ShowFlyout(bool activate = true)
    {
        Refresh();
        Position();
        // Showing a hover preview must not interrupt the application currently in focus.
        AppWindow.Show(activate);
        IsShown = true;
        IsHoverPreview = !activate;
        if (activate)
        {
            Activate();
        }

        _progressTimer.Start();
    }

    public void HideFlyout()
    {
        _progressTimer.Stop();
        AppWindow.Hide();
        IsShown = false;
        IsHoverPreview = false;
        _hiddenAtTicks = Environment.TickCount64;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!IsShown)
        {
            return;
        }

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Hover previews were never activated, so their lifetime is controlled by the mouse.
            if (!IsHoverPreview)
            {
                HideFlyout();
            }
        }
        else if (IsHoverPreview)
        {
            // A click inside the preview activates the window and pins it until normal dismissal.
            IsHoverPreview = false;
        }
    }

    private void Position()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var width = (int)(BaseWidth * scale);
        var height = (int)(BaseHeight * scale);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)(12 * scale);
        var x = area.X + area.Width - width - margin;
        var y = area.Y + area.Height - height - margin;
        if (_trayRect() is { } icon && icon.Width > 0)
        {
            x = Math.Clamp(icon.X + (icon.Width / 2) - (width / 2), area.X + margin, area.X + area.Width - width - margin);
        }

        // Auto-hidden taskbars overlay the bottom edge when they slide into view. Keep the flyout
        // above the taskbar reveal area instead of letting the taskbar cover the tray icon target.
        if (GetAutoHideBottomTaskbarTop() is { } taskbarTop)
        {
            y = Math.Max(area.Y + margin, Math.Min(y, taskbarTop - height - margin));
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>Called on the UI thread whenever the status changes (and every 500 ms while shown).</summary>
    public void Refresh()
    {
        var status = _host.Status;
        var session = status.Session;
        var md = session.Metadata;
        var view = TransportLogic.Describe(session);
        if (_pendingPlayback is not null &&
            (_pendingPlayback.IsConfirmed(view.IsPlaying, md.Title) ||
             _pendingPlayback.HasTimedOut(DateTimeOffset.UtcNow)))
        {
            _pendingPlayback = null;
        }

        _updating = true;
        try
        {
            SetMetadataText(TitleText, string.IsNullOrEmpty(md.Title) ? "Nothing playing" : md.Title);
            SetMetadataText(ArtistText, md.Artist);
            SetMetadataText(AlbumText, md.Album);
            PlayPauseIcon.Glyph = view.IsPlaying ? "" : "";
            var pending = _pendingPlayback is not null;
            PlayPauseSpinner.IsActive = pending;
            PlayPauseSpinner.Visibility = PlayPauseTrack.Visibility =
                pending ? Visibility.Visible : Visibility.Collapsed;
            PlayPauseIcon.Visibility = pending ? Visibility.Collapsed : Visibility.Visible;
            // WinUI dims children of disabled controls. Block clicks without dimming the loader.
            PlayPauseButton.IsEnabled = view.CanPlayPause || pending;
            PlayPauseButton.IsHitTestVisible = !pending;
            PlayPauseButton.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                pending ? "AuroraSelected" : "AuroraAccentGradient"];
            PlayPauseButton.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AuroraAccent"];
            PlayPauseButton.BorderThickness = new Thickness(pending ? 1.5 : 0);
            AutomationProperties.SetName(PlayPauseButton, pending ? _pendingPlayback!.Label : "Play or pause");
            PrevButton.IsEnabled = view.CanPrevious;
            NextButton.IsEnabled = view.CanNext;

            var duration = md.Progress?.TrackDurationMs ?? 0;
            var position = md.PositionMs(_host.ServerNowMicros) ?? 0;
            SeekSlider.IsEnabled = view.CanSeek && duration > 0;
            SeekSlider.Maximum = Math.Max(duration, 1);
            if (!_seekDebounce.IsRunning)
            {
                SeekSlider.Value = Math.Min(position, SeekSlider.Maximum);
            }

            ElapsedText.Text = ProgressFormatter.Time(position);
            TotalText.Text = duration > 0 ? ProgressFormatter.Time(duration) : "—";

            VolumeSlider.Value = status.Volume;
            VolumeText.Text = status.Volume.ToString();
            MuteIcon.Glyph = status.Muted || status.Volume == 0 ? "" : "";

            UpdateArtwork(session.Artwork);
        }
        finally
        {
            _updating = false;
        }

        // The queue follows the playing track: reload it when the track changes while the queue is on screen.
        if (_showingQueue && _queueTrackKey != TrackKey(md))
        {
            _ = LoadQueueAsync();
        }
    }

    private static string TrackKey(MetadataState md) => $"{md.Title}\u0001{md.Artist}\u0001{md.Album}";

    private void OnShowNowPlaying(object sender, RoutedEventArgs e) => SetView(queue: false);

    private void OnShowQueue(object sender, RoutedEventArgs e) => SetView(queue: true);

    private void SetView(bool queue)
    {
        _showingQueue = queue;
        NowTab.IsChecked = !queue;
        QueueTab.IsChecked = queue;
        NowView.Visibility = queue ? Visibility.Collapsed : Visibility.Visible;
        QueueView.Visibility = queue ? Visibility.Visible : Visibility.Collapsed;
        if (queue)
        {
            _ = LoadQueueAsync();
        }
    }

    /// <summary>Shows the Music Assistant queue of this PC's player, with the current track highlighted.</summary>
    private async Task LoadQueueAsync()
    {
        var loadId = ++_queueLoadId;
        _queueTrackKey = TrackKey(_host.Status.Session.Metadata);
        try
        {
            if (_library() is not { } library)
            {
                ShowQueueRows([], "Connect to Music Assistant to see the queue.");
                return;
            }

            _queueTarget ??= (await library.GetTargetsAsync()).FirstOrDefault(t => t.IsThisPc);
            if (_queueTarget is null)
            {
                ShowQueueRows([], "The queue is not available yet.");
                return;
            }

            var view = await library.GetQueueAsync(_queueTarget);
            if (loadId != _queueLoadId)
            {
                return;
            }

            var current = view.Queue?.CurrentIndex;
            ShowQueueRows(
                view.Items.Select(q => new ListRow(q.Name, q.Subtitle ?? string.Empty, q.ImageUrl, null, q, isCurrent: q.Index == current)).ToList(),
                "The queue is empty.");
        }
        catch (Exception ex) when (ex is MaException or HttpRequestException or TaskCanceledException)
        {
            if (loadId == _queueLoadId)
            {
                _queueTarget = null;
                ShowQueueRows([], "Couldn't load the queue.");
            }
        }
    }

    private void ShowQueueRows(List<ListRow> rows, string emptyText)
    {
        QueueList.ItemsSource = rows;
        QueueEmptyText.Text = emptyText;
        QueueEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var playing = rows.FirstOrDefault(r => r.IsCurrent);
        if (playing is not null)
        {
            QueueList.ScrollIntoView(playing, ScrollIntoViewAlignment.Leading);
        }
    }

    private async void OnQueueItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ListRow { QueueItem: { } item } || _queueTarget is null || _library() is not { } library)
        {
            return;
        }

        try
        {
            await library.PlayQueueIndexAsync(_queueTarget, item.Index);
        }
        catch (Exception ex) when (ex is MaException or HttpRequestException or TaskCanceledException)
        {
            // The queue reloads on the next track change; a failed jump needs no extra UI.
        }
    }

    /// <summary>Keep the full metadata on hover without letting it resize the player.</summary>
    private static void SetMetadataText(TextBlock block, string? value)
    {
        var text = value ?? string.Empty;
        if (block.Text == text)
        {
            return;
        }

        block.Text = text;
        ToolTipService.SetToolTip(block, text.Length == 0 ? null : text);
    }

    private void UpdateArtwork(ArtworkImage? artwork)
    {
        if (artwork is null)
        {
            ArtworkImage.Source = null;
            _artworkVersion = -1;
            return;
        }

        if (artwork.Version == _artworkVersion)
        {
            return;
        }

        _artworkVersion = artwork.Version;
        _ = LoadArtworkAsync(artwork);
    }

    private async Task LoadArtworkAsync(ArtworkImage artwork)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(artwork.Data.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 192 };
            await bitmap.SetSourceAsync(stream);
            if (artwork.Version == _artworkVersion)
            {
                ArtworkImage.Source = bitmap;
            }
        }
        catch (Exception)
        {
            ArtworkImage.Source = null;
        }
    }

    private void OnRootTapped(object sender, TappedRoutedEventArgs e)
    {
        // Let transport, seek/volume controls, tabs and queue item interactions keep their own clicks.
        for (var current = e.OriginalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or Slider or ListViewBase or ScrollBar)
            {
                return;
            }

            if (ReferenceEquals(current, Root))
            {
                break;
            }
        }

        e.Handled = true;
        _openFullApp();
    }

    private static int? GetAutoHideBottomTaskbarTop()
    {
        var data = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        var state = SHAppBarMessage(ABM_GETSTATE, ref data).ToUInt64();
        if ((state & ABS_AUTOHIDE) == 0)
        {
            return null;
        }

        data = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == UIntPtr.Zero ||
            data.uEdge != ABE_BOTTOM || data.rc.Bottom <= data.rc.Top)
        {
            return null;
        }

        return data.rc.Top;
    }

    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint ABS_AUTOHIDE = 0x00000001;
    private const uint ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeRect rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", EntryPoint = "SHAppBarMessage")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref AppBarData data);

    private void OnSystemVolumeChanged() => DispatcherQueue.TryEnqueue(Refresh);

    private async void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_pendingPlayback is not null)
            return;

        var session = _host.Status.Session;
        var command = TransportLogic.PlayPause(session);
        if (command is null)
            return;

        var playing = TransportLogic.Describe(session).IsPlaying;
        _pendingPlayback = new PlaybackFeedback(playing ? "Pausing playback…" : "Starting playback…",
            !playing, playing, session.Metadata.Title);
        Refresh();
        try
        {
            await Send(command);
            Refresh();
        }
        catch (Exception)
        {
            _pendingPlayback = null;
            Refresh();
        }
    }

    private async void OnPrevious(object sender, RoutedEventArgs e) => await Send(TransportLogic.Previous(_host.Status.Session));

    private async void OnNext(object sender, RoutedEventArgs e) => await Send(TransportLogic.Next(_host.Status.Session));

    private async Task Send(ControllerCommand? command)
    {
        if (command is not null)
        {
            await _host.SendCommandAsync(command);
        }
    }

    private void OnSeekChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _pendingSeekMs = (long)e.NewValue;
        ElapsedText.Text = ProgressFormatter.Time(_pendingSeekMs);
        _seekDebounce.Stop();
        _seekDebounce.Start();
    }

    private async void SendSeek() => await Send(TransportLogic.Seek(_host.Status.Session, _pendingSeekMs));

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        VolumeText.Text = ((int)e.NewValue).ToString();
        _host.Volume.SetVolume((int)e.NewValue);
    }

    private void OnMute(object sender, RoutedEventArgs e) => _host.Volume.SetMuted(!_host.Volume.Muted);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
