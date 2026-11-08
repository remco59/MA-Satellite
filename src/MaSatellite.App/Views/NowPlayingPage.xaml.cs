using System.Collections.ObjectModel;
using MaSatellite.Host.Library;
using MaSatellite.Host.NowPlaying;
using MaSatellite.Host.Presentation;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MaSatellite.App.Views;

/// <summary>What the page needs to know about the transport; read from the window's existing controls, never kept separately.</summary>
internal readonly record struct TransportState(
    bool CanPrevious,
    bool CanNext,
    bool CanPlayPause,
    bool Playing,
    bool Pending,
    string PendingLabel,
    bool ShuffleEnabled,
    bool Shuffle,
    bool RepeatEnabled,
    string Repeat,
    bool SeekEnabled);

/// <summary>
/// The unified listening page: the main player on top and Up Next / Lyrics / Related / About underneath.
/// It owns no playback state. The window feeds it what is playing and where, and it raises events for every
/// transport action so playback keeps a single implementation.
/// </summary>
public sealed partial class NowPlayingPage : UserControl
{
    private static readonly TimeSpan UserScrollPause = TimeSpan.FromSeconds(5);

    private readonly ObservableCollection<QueueRow> _upcoming = [];
    private NowPlayingController? _controller;
    private Func<PlayTarget?>? _target;
    private string _tab = "up";
    private bool _narrow;
    private bool _updating;
    private bool _favorite;
    private bool _shown;
    private bool _queueDirty = true;
    private int _queueRequest;
    private DateTimeOffset _userScrolledAt = DateTimeOffset.MinValue;
    private IReadOnlyList<MaItem>? _playlists;
    private DateTimeOffset _playlistsAt;

    // Identity of the track the tabs currently describe.
    private string _trackKey = string.Empty;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string? _album;
    private string? _trackUri;
    private int? _durationSeconds;
    private Task _context = Task.CompletedTask;
    private CancellationTokenSource _trackCts = new();

    public NowPlayingPage()
    {
        InitializeComponent();
        PageScroll.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => _userScrolledAt = DateTimeOffset.UtcNow), handledEventsToo: true);
        QueueList.ItemsSource = _upcoming;
        SelectTab("up", animate: false);
    }

    public event EventHandler? PlayPauseRequested;

    public event EventHandler? NextRequested;

    public event EventHandler? PreviousRequested;

    public event EventHandler? ShuffleRequested;

    public event EventHandler? RepeatRequested;

    /// <summary>Seek target in milliseconds.</summary>
    public event EventHandler<long>? SeekRequested;

    public event EventHandler<FrameworkElement>? TransferRequested;

    public event EventHandler<FrameworkElement>? DeviceRequested;

    /// <summary>Queue index to start playing.</summary>
    public event EventHandler<int>? PlayIndexRequested;

    public event EventHandler<string>? InfoRaised;

    public event EventHandler<MaException>? ErrorRaised;

    /// <summary>A card or row of a related item was opened (the window shows its detail page).</summary>
    public event ItemClickEventHandler? ItemClick;

    public event EventHandler<RowAction>? RowPlayRequested;

    public event EventHandler<RowAction>? RowMenuRequested;

    /// <summary>Open the detail page of one item.</summary>
    public event EventHandler<MaItem>? OpenItemRequested;

    /// <summary>The window's shared card template, so related albums and artists look like everywhere else.</summary>
    public DataTemplate? CardTemplate { get; set; }

    internal void Initialize(NowPlayingController controller, Func<PlayTarget?> target)
    {
        _controller = controller;
        _target = target;
    }

    private PlayTarget? Target => _target?.Invoke();

    private static Brush R(string key) => (Brush)Application.Current.Resources[key];

    // ---- Fed by the window ----

    internal void SetPlayback(PlaybackSummary summary, string? destination)
    {
        TitleText.Text = summary.Title;
        ArtistText.Text = summary.Artist;
        DeviceText.Text = destination ?? "No player";

        var key = summary.HasTrack ? $"{summary.Title}\u001f{summary.Artist}" : string.Empty;
        if (key == _trackKey)
        {
            return;
        }

        _trackKey = key;
        _title = summary.HasTrack ? summary.Title : string.Empty;
        _artist = summary.HasTrack ? summary.Artist : string.Empty;
        OnTrackChanged();
    }

    internal void SetArtwork(ImageSource? image)
    {
        Artwork.Source = image;
        if (image is not null)
        {
            Motion.FadeIn(Artwork);
        }
    }

    internal void SetStatus(string text, Brush foreground)
    {
        StateText.Text = string.IsNullOrEmpty(text) ? string.Empty : "· " + text;
        StateText.Foreground = foreground;
    }

    internal void SetTint(Brush? tint) => HeroCard.Background = tint ?? R("AuroraHeroGradient");

    internal void SetTransport(TransportState t)
    {
        PrevButton.IsEnabled = t.CanPrevious;
        NextButton.IsEnabled = t.CanNext;
        PlayButton.IsEnabled = t.CanPlayPause || t.Pending;
        PlayButton.IsHitTestVisible = !t.Pending;
        PlayIcon.Glyph = t.Playing ? "" : "";
        PlayIcon.Visibility = t.Pending ? Visibility.Collapsed : Visibility.Visible;
        PlaySpinner.IsActive = t.Pending;
        PlaySpinner.Visibility = t.Pending ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, t.Pending ? t.PendingLabel : "Play or pause");

        ShuffleButton.IsEnabled = t.ShuffleEnabled;
        ShuffleIcon.Foreground = R(t.Shuffle ? "AuroraAccent" : "AuroraMuted");
        ToolTipService.SetToolTip(ShuffleButton, t.Shuffle ? "Shuffle on" : "Shuffle off");
        RepeatButton.IsEnabled = t.RepeatEnabled;
        RepeatIcon.Foreground = R(t.Repeat == "off" ? "AuroraMuted" : "AuroraAccent");
        RepeatIcon.Glyph = t.Repeat == "one" ? "" : "";
        ToolTipService.SetToolTip(RepeatButton, $"Repeat: {t.Repeat}");
        SeekSlider.IsEnabled = t.SeekEnabled;
    }

    internal void SetProgress(long positionMs, long durationMs)
    {
        _updating = true;
        try
        {
            SeekSlider.Maximum = Math.Max(1, durationMs);
            SeekSlider.Value = Math.Clamp(positionMs, 0, Math.Max(1, durationMs));
            ElapsedText.Text = ProgressFormatter.Time(Math.Max(0, positionMs));
            DurationText.Text = durationMs > 0 ? ProgressFormatter.Time(durationMs) : "—";
        }
        finally
        {
            _updating = false;
        }

        if (durationMs > 0 && _durationSeconds is null)
        {
            _durationSeconds = (int)Math.Round(durationMs / 1000.0);
        }

        FollowLyrics(positionMs / 1000.0);
    }

    /// <summary>The queue changed while the page was hidden; it reloads when the page is shown.</summary>
    internal void MarkQueueStale() => _queueDirty = true;

    /// <summary>The page became the visible destination (or stopped being it).</summary>
    internal async Task SetShownAsync(bool shown)
    {
        _shown = shown;
        if (!shown)
        {
            return;
        }

        if (_queueDirty)
        {
            await RefreshQueueAsync();
        }

        await EnsureTabLoadedAsync();
    }

    internal async Task ShowTabAsync(string tab)
    {
        SelectTab(tab, animate: true);
        await EnsureTabLoadedAsync();
    }

    /// <summary>Everything about the previous player is stale (the user chose another player).</summary>
    internal async Task ResetForTargetChangeAsync()
    {
        _queueDirty = true;
        _upcoming.Clear();
        OnTrackChanged();
        if (_shown)
        {
            await RefreshQueueAsync();
            await EnsureTabLoadedAsync();
        }
    }

    // ---- Layout ----

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        QueueList.ItemTemplate = (DataTemplate)Resources[width < 880 ? "QueueRowNarrow" : "QueueRowWide"];

        var narrow = width < 760;
        if (_layoutApplied && narrow == _narrow)
        {
            return;
        }

        _layoutApplied = true;
        _narrow = narrow;
        ArtFrame.Width = ArtFrame.Height = narrow ? Math.Clamp(width - 96, 160, 260) : 280;
        HeroCard.Padding = new Thickness(narrow ? 18 : 28);
        TitleText.FontSize = narrow ? 28 : 36;
        Hero.RowDefinitions.Clear();
        if (narrow)
        {
            // Artwork, then text, then controls: one column.
            Hero.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            Hero.ColumnDefinitions[1].Width = new GridLength(0);
            for (var i = 0; i < 3; i++)
            {
                Hero.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            ArtFrame.HorizontalAlignment = HorizontalAlignment.Center;
            Place(ArtFrame, column: 0, row: 0, rowSpan: 1);
            Place(HeroText, column: 0, row: 1, rowSpan: 1);
            Place(HeroControls, column: 0, row: 2, rowSpan: 1);
        }
        else
        {
            // Artwork on the left, text and controls on the right.
            Hero.ColumnDefinitions[0].Width = GridLength.Auto;
            Hero.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            Hero.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Hero.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ArtFrame.HorizontalAlignment = HorizontalAlignment.Left;
            Place(ArtFrame, column: 0, row: 0, rowSpan: 2);
            Place(HeroText, column: 1, row: 0, rowSpan: 1);
            Place(HeroControls, column: 1, row: 1, rowSpan: 1);
        }
    }

    private static void Place(FrameworkElement element, int column, int row, int rowSpan)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        Grid.SetRowSpan(element, rowSpan);
    }

    private bool _layoutApplied;

    // ---- Hero actions ----

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private void OnNextClick(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    private void OnPreviousClick(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void OnShuffleClick(object sender, RoutedEventArgs e) => ShuffleRequested?.Invoke(this, EventArgs.Empty);

    private void OnRepeatClick(object sender, RoutedEventArgs e) => RepeatRequested?.Invoke(this, EventArgs.Empty);

    private void OnDeviceClick(object sender, RoutedEventArgs e) => DeviceRequested?.Invoke(this, (FrameworkElement)sender);

    private void OnTransferClick(object sender, RoutedEventArgs e) => TransferRequested?.Invoke(this, (FrameworkElement)sender);

    private void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating || !SeekSlider.IsEnabled)
        {
            return;
        }

        ElapsedText.Text = ProgressFormatter.Time((long)e.NewValue);
        SeekRequested?.Invoke(this, (long)e.NewValue);
    }

    private async void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null || string.IsNullOrEmpty(_trackUri))
        {
            InfoRaised?.Invoke(this, "Favourites need the track to be in your Music Assistant queue.");
            return;
        }

        var wanted = !_favorite;
        try
        {
            _favorite = await _controller.SetFavoriteAsync(_trackUri, wanted);
            ApplyFavorite();
            InfoRaised?.Invoke(this, _favorite ? "Added to your favourites." : "Removed from your favourites.");
        }
        catch (MaException ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
    }

    private void ApplyFavorite()
    {
        FavoriteIcon.Glyph = _favorite ? "" : "";
        FavoriteIcon.Foreground = _favorite ? R("AuroraAccent") : R("AuroraMuted");
        ToolTipService.SetToolTip(FavoriteButton, _favorite ? "Remove from favourites" : "Add to favourites");
    }

    private async void OnAddToPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_trackUri))
        {
            InfoRaised?.Invoke(this, "Adding to a playlist needs the track to be in your Music Assistant queue.");
            return;
        }

        await ShowPlaylistFlyoutAsync((FrameworkElement)sender, _trackUri, _title);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuEntry("Track details", () =>
        {
            SelectTab("about", animate: true);
            return EnsureTabLoadedAsync();
        }));
        flyout.Items.Add(MenuEntry("Copy title and artist", () =>
        {
            var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText($"{_artist} – {_title}");
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            return Task.CompletedTask;
        }));
        flyout.ShowAt((FrameworkElement)sender);
    }

    private static MenuFlyoutItem MenuEntry(string text, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += async (_, _) => await action();
        return item;
    }

    /// <summary>Lists library playlists to add one track to. The server decides which of them are editable.</summary>
    private async Task ShowPlaylistFlyoutAsync(FrameworkElement anchor, string trackUri, string trackName)
    {
        if (_controller is null)
        {
            return;
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuFlyoutItem { Text = "Loading playlists…", IsEnabled = false });
        flyout.ShowAt(anchor);
        try
        {
            if (_playlists is null || DateTimeOffset.UtcNow - _playlistsAt > TimeSpan.FromMinutes(1))
            {
                _playlists = await _controller.Library.GetPlaylistsAsync();
                _playlistsAt = DateTimeOffset.UtcNow;
            }

            flyout.Items.Clear();
            if (_playlists.Count == 0)
            {
                flyout.Items.Add(new MenuFlyoutItem { Text = "No playlists in your library", IsEnabled = false });
            }

            foreach (var playlist in _playlists.Take(30))
            {
                flyout.Items.Add(MenuEntry(playlist.Name, async () =>
                {
                    try
                    {
                        await _controller.Library.AddToPlaylistAsync(playlist, trackUri);
                        InfoRaised?.Invoke(this, $"Added '{trackName}' to {playlist.Name}.");
                    }
                    catch (MaException ex)
                    {
                        ErrorRaised?.Invoke(this, ex);
                    }
                }));
            }
        }
        catch (MaException ex)
        {
            flyout.Hide();
            ErrorRaised?.Invoke(this, ex);
        }
    }

    // ---- Tabs ----

    private async void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab })
        {
            SelectTab(tab, animate: true);
            await EnsureTabLoadedAsync();
        }
    }

    private void SelectTab(string tab, bool animate)
    {
        _tab = tab;
        var panels = new (string Key, UIElement Panel, Button Button, UIElement Underline)[]
        {
            ("up", UpPanel, TabUp, UnderlineUp),
            ("lyrics", LyricsPanel, TabLyrics, UnderlineLyrics),
            ("related", RelatedPanel, TabRelated, UnderlineRelated),
            ("about", AboutPanel, TabAbout, UnderlineAbout),
        };

        foreach (var (key, panel, button, underline) in panels)
        {
            var on = key == tab;
            button.Foreground = R(on ? "AuroraText" : "AuroraMuted");
            underline.Opacity = on ? 1 : 0;
            if (on && panel.Visibility != Visibility.Visible)
            {
                panel.Visibility = Visibility.Visible;
                if (animate)
                {
                    Motion.FadeIn(panel, 200);
                }
            }
            else if (!on)
            {
                panel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private Task EnsureTabLoadedAsync() => _tab switch
    {
        "up" => Task.CompletedTask,
        "lyrics" => LoadLyricsAsync(),
        "related" => LoadRelatedAsync(),
        "about" => LoadAboutAsync(),
        _ => Task.CompletedTask,
    };

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        _lyricsFor = _relatedFor = _aboutFor = null;
        _ = EnsureTabLoadedAsync();
    }

    /// <summary>A different track is playing: cancel pending lookups and mark every tab as needing a fresh load.</summary>
    private void OnTrackChanged()
    {
        _trackCts.Cancel();
        _trackCts.Dispose();
        _trackCts = new CancellationTokenSource();
        _trackUri = null;
        _album = null;
        _durationSeconds = null;
        _lyricsFor = _relatedFor = _aboutFor = null;
        _favorite = false;
        ApplyFavorite();
        MetaText.Visibility = Visibility.Collapsed;
        ClearLyrics();
        RelatedHost.Children.Clear();
        AboutHost.Children.Clear();
        if (_shown)
        {
            _context = LoadTrackContextAsync(_trackCts.Token);
            _ = EnsureTabLoadedAsync();
        }
        else
        {
            _queueDirty = true;
        }
    }

    /// <summary>Finds the Music Assistant record of the playing track (for ids, album, favourites) without blocking anything.</summary>
    private async Task LoadTrackContextAsync(CancellationToken ct)
    {
        try
        {
            await RefreshQueueAsync();
            if (_controller is null || ct.IsCancellationRequested || string.IsNullOrEmpty(_trackUri))
            {
                return;
            }

            var details = await _controller.GetDetailsAsync(_trackUri, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            _favorite = details.IsFavorite;
            ApplyFavorite();
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(details.AlbumName))
            {
                meta.Add(details.AlbumName);
            }

            if (details.Item.Year is { } year)
            {
                meta.Add(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            meta.AddRange(details.Genres.Take(2));
            MetaText.Text = string.Join(" · ", meta);
            MetaText.Visibility = meta.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is MaException or OperationCanceledException)
        {
            // Secondary information only; the player itself must never depend on it.
        }
    }

    // ---- Up Next ----

    internal async Task RefreshQueueAsync()
    {
        if (_controller is null || Target is not { } target)
        {
            ShowUpStatus("Choose a player to see its queue.", busy: false, retry: false);
            return;
        }

        var request = ++_queueRequest;
        var first = _upcoming.Count == 0 && CurrentCard.Visibility == Visibility.Collapsed;
        if (first)
        {
            ShowUpStatus("Loading the queue…", busy: true, retry: false);
        }

        try
        {
            var view = await _controller.Library.GetQueueAsync(target);
            if (request != _queueRequest || Target?.QueueId != target.QueueId)
            {
                return;
            }

            ApplyQueue(view);
            _queueDirty = false;
        }
        catch (MaException ex)
        {
            if (request == _queueRequest)
            {
                ShowUpStatus("The queue could not be loaded. " + ex.Message, busy: false, retry: true);
            }
        }
    }

    private void ApplyQueue(QueueView view)
    {
        var currentIndex = view.Queue?.CurrentIndex;
        var current = view.Queue?.CurrentItem is { Name.Length: > 0 } c
            ? c
            : view.Items.FirstOrDefault(i => i.Index == currentIndex);
        var upcoming = view.Items.Where(i => currentIndex is null || i.Index > currentIndex).OrderBy(i => i.Index).ToList();

        if (current is not null)
        {
            CurrentCard.Visibility = Visibility.Visible;
            CurrentTitle.Text = current.Name;
            CurrentSub.Text = string.IsNullOrEmpty(current.Album) ? current.Artist ?? current.Subtitle ?? string.Empty : $"{current.Artist} · {current.Album}";
            CurrentArt.Source = new QueueRow(current).Image;
            if (!string.IsNullOrEmpty(current.Uri) && current.Uri != _trackUri)
            {
                _trackUri = current.Uri;
                _album = current.Album;
                _durationSeconds ??= current.DurationSeconds;
            }
        }
        else
        {
            CurrentCard.Visibility = Visibility.Collapsed;
        }

        // Only rebuild when the list really changed, so a drag in progress or the scroll position is not disturbed.
        var same = _upcoming.Count == upcoming.Count
            && _upcoming.Select(r => r.Item.QueueItemId).SequenceEqual(upcoming.Select(i => i.QueueItemId));
        if (!same)
        {
            _upcoming.Clear();
            for (var i = 0; i < upcoming.Count; i++)
            {
                _upcoming.Add(new QueueRow(upcoming[i]) { UpcomingIndex = i });
            }
        }

        var seconds = upcoming.Sum(i => i.DurationSeconds ?? 0);
        UpSubtitle.Text = upcoming.Count == 0
            ? "Nothing queued after this track."
            : $"{upcoming.Count} {(upcoming.Count == 1 ? "track" : "tracks")}" + (seconds > 0 ? $" · {FormatTotal(seconds)}" : string.Empty);
        ClearButton.IsEnabled = view.Items.Count > 0;

        if (upcoming.Count == 0)
        {
            ShowUpStatus(current is null ? "The queue is empty. Play something and it will show up here." : "That was the last track. Add more from your library.", busy: false, retry: false);
        }
        else
        {
            UpStatus.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatTotal(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
    }

    private void ShowUpStatus(string text, bool busy, bool retry)
    {
        UpStatus.Visibility = Visibility.Visible;
        UpStatusText.Text = text;
        UpBusy.IsActive = busy;
        UpBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnUpRetryClick(object sender, RoutedEventArgs e) => await RefreshQueueAsync();

    private void OnQueueItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QueueRow row)
        {
            PlayIndexRequested?.Invoke(this, row.Item.Index);
        }
    }

    /// <summary>A finished drag is turned into one relative move; the server's queue event then confirms the order.</summary>
    private async void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_controller is null || Target is not { } target
            || args.DropResult != global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move
            || args.Items.Count != 1 || args.Items[0] is not QueueRow row)
        {
            return;
        }

        var newIndex = _upcoming.IndexOf(row);
        var shift = newIndex - row.UpcomingIndex;
        if (newIndex < 0 || shift == 0)
        {
            return;
        }

        try
        {
            await _controller.Library.MoveQueueItemAsync(target, row.Item.QueueItemId, shift);
            for (var i = 0; i < _upcoming.Count; i++)
            {
                _upcoming[i].UpcomingIndex = i;
            }
        }
        catch (MaException ex)
        {
            ErrorRaised?.Invoke(this, ex);
            await RefreshQueueAsync();
        }
    }

    private void OnRowMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QueueRow row } anchor)
        {
            return;
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuEntry("Play now", () =>
        {
            PlayIndexRequested?.Invoke(this, row.Item.Index);
            return Task.CompletedTask;
        }));
        flyout.Items.Add(MenuEntry("Play next", () => QueueEditAsync(t => _controller!.Library.MoveQueueItemAsync(t, row.Item.QueueItemId, 0))));
        flyout.Items.Add(MenuEntry("Remove from queue", () => QueueEditAsync(t => _controller!.Library.RemoveQueueItemAsync(t, row.Item.QueueItemId))));
        flyout.Items.Add(new MenuFlyoutSeparator());
        var playlist = new MenuFlyoutItem { Text = "Add to playlist…", IsEnabled = !string.IsNullOrEmpty(row.Item.Uri) };
        playlist.Click += async (_, _) => await ShowPlaylistFlyoutAsync(anchor, row.Item.Uri!, row.Title);
        flyout.Items.Add(playlist);
        var details = new MenuFlyoutItem { Text = "Track details", IsEnabled = !string.IsNullOrEmpty(row.Item.Uri) };
        details.Click += async (_, _) => await OpenItemByUriAsync(row.Item.Uri!);
        flyout.Items.Add(details);
        flyout.ShowAt(anchor);
    }

    private async Task QueueEditAsync(Func<PlayTarget, Task> edit)
    {
        if (_controller is null || Target is not { } target)
        {
            return;
        }

        try
        {
            await edit(target);
            await RefreshQueueAsync();
        }
        catch (MaException ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
    }

    private async Task OpenItemByUriAsync(string uri)
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            var details = await _controller.GetDetailsAsync(uri);
            OpenItemRequested?.Invoke(this, details.Item);
        }
        catch (MaException ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null || Target is not { } target)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear the queue?",
            Content = $"This removes every track from the queue of {target.Name}. The track that is playing will stop.",
            PrimaryButtonText = "Clear queue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await QueueEditAsync(t => _controller.Library.ClearQueueAsync(t));
    }
}
