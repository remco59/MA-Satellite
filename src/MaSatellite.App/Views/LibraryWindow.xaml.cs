using System.Runtime.InteropServices.WindowsRuntime;
using MaSatellite.Core.Session;
using MaSatellite.Host.Library;
using MaSatellite.Host.Orchestration;
using MaSatellite.Host.Presentation;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace MaSatellite.App.Views;

/// <summary>Browse the Music Assistant library, search, play on any player and manage queues.</summary>
public sealed partial class LibraryWindow : Window
{
    private readonly LibraryController _controller;
    private readonly SatelliteHost _host;
    private readonly Action _openSettings;
    private readonly DispatcherQueueTimer _queueRefresh;
    private readonly Stack<MaItem> _history = new();
    private IReadOnlyList<PlayTarget> _targets = [];
    private MaItem? _openedItem;
    private string _tab = "home";
    private MaItem? _featuredAlbum;
    private string _lastQuery = string.Empty;
    private int _loadId;
    private bool _loadingTargets;
    private bool _closed;
    private int _playbackRefreshRequest;
    private int _artworkRequestId;
    private string? _artworkCacheKey;

    internal LibraryWindow(LibraryController controller, SatelliteHost host, Action openSettings)
    {
        _controller = controller;
        _host = host;
        _openSettings = openSettings;
        InitializeComponent();
        AuroraWindowChrome.Apply(this);
        Title = "ma-satellite library";
        AppWindow.Resize(new SizeInt32(1180, 790));

        _queueRefresh = DispatcherQueue.CreateTimer();
        _queueRefresh.Interval = TimeSpan.FromMilliseconds(600);
        _queueRefresh.IsRepeating = false;
        _queueRefresh.Tick += (_, _) =>
        {
            if (_tab is "queue" or "now")
            {
                _ = LoadTabAsync();
            }
            else
            {
                _ = RefreshPlaybackSummaryAsync();
            }
        };

        _controller.Service.ServerEvent += OnServerEvent;
        Closed += (_, _) =>
        {
            _closed = true;
            _queueRefresh.Stop();
            ++_artworkRequestId;
            _controller.Service.ServerEvent -= OnServerEvent;
        };
        Activated += OnFirstActivated;
    }

    private PlayTarget? SelectedTarget => TargetBox.SelectedItem is ComboBoxItem { Tag: PlayTarget t } ? t : null;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= OnFirstActivated;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await LoadTargetsAsync();
        await LoadTabAsync();
    }

    private async Task LoadTargetsAsync()
    {
        _loadingTargets = true;
        try
        {
            _targets = await _controller.GetTargetsAsync();
            var previous = SelectedTarget?.QueueId;
            TargetBox.Items.Clear();
            foreach (var t in _targets)
            {
                TargetBox.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t });
            }

            var index = previous is null ? 0 : Math.Max(_targets.ToList().FindIndex(t => t.QueueId == previous), 0);
            if (_targets.Count > 0)
            {
                TargetBox.SelectedIndex = index;
            }

            RefreshTransferTargets();
            SidebarStatus.Text = _targets.Count > 0 ? $"{_targets.Count} player(s) available" : "No players available";
        }
        catch (MaException ex)
        {
            ShowError(ex);
        }
        finally
        {
            _loadingTargets = false;
        }
    }

    private void RefreshTransferTargets()
    {
        TransferBox.Items.Clear();
        foreach (var t in _targets.Where(t => t.QueueId != SelectedTarget?.QueueId))
        {
            TransferBox.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t });
        }
    }

    private async void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab })
        {
            await NavigateAsync(tab);
        }
    }

    // All navigation comes through one path so focus, selection and detail history stay in sync.
    private async Task NavigateAsync(string tab, bool forceReload = false)
    {
        if (!forceReload && tab == _tab && _openedItem is null)
        {
            return;
        }

        _tab = tab;
        _openedItem = null;
        _history.Clear();
        await LoadTabAsync();
    }

    private async void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _lastQuery = args.QueryText?.Trim() ?? string.Empty;
        await NavigateAsync("search", forceReload: true);
    }

    private async void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTargets)
        {
            return;
        }

        RefreshTransferTargets();
        if (_tab is "queue" or "now")
        {
            await LoadTabAsync();
        }
        else
        {
            await RefreshPlaybackSummaryAsync();
        }
    }

    private void UpdateNavigationState()
    {
        var selected = (Style)Application.Current.Resources["AuroraNavSelectedButtonStyle"];
        var regular = (Style)Application.Current.Resources["AuroraNavButtonStyle"];
        foreach (var button in new[] { NavHome, NavLibrary, NavAlbum, NavArtist, NavTrack, NavPlaylist, NavNow, NavQueue, NavSearch })
        {
            // The Library parent remains active while browsing Albums, Artists, or Tracks.
            var active = button == NavLibrary
                ? _tab is "album" or "artist" or "track"
                : (string?)button.Tag == _tab;
            button.Style = active ? selected : regular;
        }
    }

    private async Task LoadTabAsync()
    {
        var id = ++_loadId;
        Message.IsOpen = false;
        EmptyText.Visibility = Visibility.Collapsed;
        Busy.IsActive = true;
        UpdateNavigationState();
        ContextBar.Visibility = _openedItem is null ? Visibility.Collapsed : Visibility.Visible;
        ContextActions.Visibility = _openedItem?.Type == MaMediaType.Artist ? Visibility.Collapsed : Visibility.Visible;
        QueueBar.Visibility = (_tab is "queue" or "now") && _openedItem is null ? Visibility.Visible : Visibility.Collapsed;
        NowPlayingHero.Visibility = _tab == "now" && _openedItem is null ? Visibility.Visible : Visibility.Collapsed;
        HomeHero.Visibility = _tab == "home" && _openedItem is null ? Visibility.Visible : Visibility.Collapsed;
        var useGrid = (_tab is "home" or "album") && _openedItem is null;
        AlbumGrid.Visibility = useGrid ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = useGrid ? Visibility.Collapsed : Visibility.Visible;
        ContentHeading.Text = _openedItem?.Name ?? (_tab switch
        {
            "now" => "Now playing",
            "home" => "Welcome back",
            "queue" => "Your queue",
            "search" => "Discover music",
            "artist" => "Artists",
            "playlist" => "Your playlists",
            "track" => "Your tracks",
            _ => "Your library",
        });
        ContentDescription.Text = _openedItem is not null ? "Explore and play your selection." : _tab switch
        {
            "now" => "Your current Music Assistant playback and what's up next.",
            "home" => "Your music, beautifully organised.",
            "queue" => "Manage what's coming up next.",
            "search" => "Find music across your library and providers.",
            _ => "Everything you love, beautifully organised.",
        };
        try
        {
            List<ListRow> rows;
            string empty = "Nothing here yet.";
            if (_openedItem is { } parent)
            {
                ContextTitle.Text = parent.Name;
                var client = await _controller.Service.GetClientAsync();
                rows = (await client.GetChildrenAsync(parent)).Select(i => ListRow.From(i)).ToList();
            }
            else if (_tab is "queue" or "now")
            {
                rows = await LoadQueueRowsAsync();
                empty = "The queue is empty.";
            }
            else if (_tab == "search")
            {
                var client = await _controller.Service.GetClientAsync();
                var results = await client.SearchAsync(_lastQuery);
                rows = results.Albums.Concat(results.Artists).Concat(results.Tracks).Concat(results.Playlists).Select(i => ListRow.From(i, showType: true)).ToList();
                empty = string.IsNullOrEmpty(_lastQuery) ? "Type a search above." : "No results.";
            }
            else
            {
                var type = _tab switch { "artist" => MaMediaType.Artist, "playlist" => MaMediaType.Playlist, "track" => MaMediaType.Track, _ => MaMediaType.Album };
                var client = await _controller.Service.GetClientAsync();
                rows = (await client.GetLibraryAsync(type, limit: 300)).Select(i => ListRow.From(i)).ToList();
                empty = "Your library has no items of this kind.";
            }

            if (id != _loadId)
            {
                return;
            }

            // Use actual library data for Home; there are no demo tracks or fake buttons.
            var featured = _tab == "home" ? rows.FirstOrDefault(r => r.Item?.IsPlayable == true) : null;
            _featuredAlbum = featured?.Item;
            HomeTitle.Text = featured?.Title ?? "Your music, ready to play";
            HomeArtist.Text = featured?.Subtitle ?? "Explore your albums and playlists.";
            HomeArtwork.Source = featured?.Image;
            HomePlayButton.IsEnabled = _featuredAlbum is not null && SelectedTarget is not null;
            // Only realize the active visual tree; both controls use virtualization.
            AlbumGrid.ItemsSource = useGrid ? rows : null;
            List.ItemsSource = useGrid ? null : rows;
            EmptyText.Text = empty;
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            await RefreshPlaybackSummaryAsync();
        }
        catch (MaException ex) when (id == _loadId)
        {
            List.ItemsSource = null;
            AlbumGrid.ItemsSource = null;
            ShowError(ex);
        }
        finally
        {
            if (id == _loadId)
            {
                Busy.IsActive = false;
            }
        }
    }

    private async Task<List<ListRow>> LoadQueueRowsAsync()
    {
        if (SelectedTarget is not { } target)
        {
            return [];
        }

        var view = await _controller.GetQueueAsync(target);
        var current = view.Queue?.CurrentIndex;
        return view.Items.Select(q => new ListRow(q.Name, q.Subtitle ?? string.Empty, q.ImageUrl, null, q, isCurrent: q.Index == current)).ToList();
    }

    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ListRow row)
        {
            return;
        }

        if (row.QueueItem is { } queueItem && SelectedTarget is { } target)
        {
            await RunAsync(() => _controller.PlayQueueIndexAsync(target, queueItem.Index));
            return;
        }

        if (row.Item is not { } item)
        {
            return;
        }

        if (item.Type is MaMediaType.Album or MaMediaType.Artist or MaMediaType.Playlist)
        {
            if (_openedItem is not null)
            {
                _history.Push(_openedItem);
            }

            _openedItem = item;
            await LoadTabAsync();
        }
        else
        {
            await PlayAsync(item, MaQueueOption.Replace);
        }
    }

    private async void OnBack(object sender, RoutedEventArgs e)
    {
        _openedItem = _history.Count > 0 ? _history.Pop() : null;
        await LoadTabAsync();
    }

    private void OnRowMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ListRow { Item: { } item } })
        {
            return;
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuItem("Play now", () => PlayAsync(item, MaQueueOption.Replace)));
        flyout.Items.Add(MenuItem("Play next", () => PlayAsync(item, MaQueueOption.Next)));
        flyout.Items.Add(MenuItem("Add to queue", () => PlayAsync(item, MaQueueOption.Add)));
        flyout.ShowAt((FrameworkElement)sender);
    }

    private static MenuFlyoutItem MenuItem(string text, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += async (_, _) => await action();
        return item;
    }

    private async void OnHomePlay(object sender, RoutedEventArgs e)
    {
        if (_featuredAlbum is { } album)
        {
            await PlayAsync(album, MaQueueOption.Replace);
        }
    }

    private async void OnContextPlay(object sender, RoutedEventArgs e) => await PlayOpenedAsync(MaQueueOption.Replace);

    private async void OnContextPlayNext(object sender, RoutedEventArgs e) => await PlayOpenedAsync(MaQueueOption.Next);

    private async void OnContextAdd(object sender, RoutedEventArgs e) => await PlayOpenedAsync(MaQueueOption.Add);

    private Task PlayOpenedAsync(MaQueueOption option) => _openedItem is { } item ? PlayAsync(item, option) : Task.CompletedTask;

    private async Task PlayAsync(MaItem item, MaQueueOption option)
    {
        if (SelectedTarget is not { } target)
        {
            ShowInfo("Choose a player to play on first.");
            return;
        }

        await RunAsync(() => _controller.PlayAsync(item, target, option));
        ShowInfo(option switch
        {
            MaQueueOption.Next => $"'{item.Name}' will play next on {target.Name}.",
            MaQueueOption.Add => $"Added '{item.Name}' to the queue of {target.Name}.",
            _ => $"Playing '{item.Name}' on {target.Name}.",
        });
    }

    private async void OnQueuePlayPause(object sender, RoutedEventArgs e) => await QueueCommandAsync("play_pause");

    private async void OnQueueNext(object sender, RoutedEventArgs e) => await QueueCommandAsync("next");

    private async void OnQueuePrevious(object sender, RoutedEventArgs e) => await QueueCommandAsync("previous");

    private async Task QueueCommandAsync(string command)
    {
        if (SelectedTarget is not { } target)
        {
            return;
        }

        // Sendspin commands are authoritative for the local player, just like the tray mini-player.
        if (target.IsThisPc && _host.Status.Connection == ConnectionState.Connected)
        {
            var session = _host.Status.Session;
            var localCommand = command switch
            {
                "play_pause" => TransportLogic.PlayPause(session),
                "next" => TransportLogic.Next(session),
                "previous" => TransportLogic.Previous(session),
                _ => null,
            };

            if (localCommand is not null)
            {
                await _host.SendCommandAsync(localCommand);
                RefreshLocalPlayback();
            }

            return;
        }

        await RunAsync(() => _controller.QueueControlAsync(target, command));
        await RefreshPlaybackSummaryAsync();
    }

    private async void OnTransfer(object sender, RoutedEventArgs e)
    {
        if (SelectedTarget is not { } from || TransferBox.SelectedItem is not ComboBoxItem { Tag: PlayTarget to })
        {
            ShowInfo("Choose the player to move the queue to.");
            return;
        }

        await RunAsync(() => _controller.TransferAsync(from, to));
        ShowInfo($"Moved the queue from {from.Name} to {to.Name}.");
        TargetBox.SelectedItem = TargetBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Tag as PlayTarget)?.QueueId == to.QueueId) ?? TargetBox.SelectedItem;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (MaException ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Called on the UI thread by SatelliteShell when Sendspin status changes.</summary>
    internal void RefreshLocalPlayback()
    {
        if (_closed || SelectedTarget is not { IsThisPc: true } target)
        {
            return;
        }

        // Invalidate in-flight queue responses from a previously selected remote player.
        ++_playbackRefreshRequest;
        ApplyPlaybackSummary(PlaybackSummaryLogic.FromLocal(_host.Status, target.Name));
    }

    private async Task RefreshPlaybackSummaryAsync()
    {
        var request = ++_playbackRefreshRequest;
        if (_closed)
        {
            return;
        }

        if (SelectedTarget is not { } target)
        {
            ApplyPlaybackSummary(new PlaybackSummary("Nothing playing", "Choose a player", null, null, false, false));
            return;
        }

        if (target.IsThisPc)
        {
            // Don't depend on the MA queue for our own player: Sendspin has the actual track.
            ApplyPlaybackSummary(PlaybackSummaryLogic.FromLocal(_host.Status, target.Name));
            return;
        }

        try
        {
            var view = await _controller.GetQueueAsync(target);
            if (_closed || request != _playbackRefreshRequest || SelectedTarget?.QueueId != target.QueueId)
            {
                return;
            }

            ApplyPlaybackSummary(PlaybackSummaryLogic.FromQueue(view));
        }
        catch (MaException)
        {
            if (_closed || request != _playbackRefreshRequest || SelectedTarget?.QueueId != target.QueueId)
            {
                return;
            }

            // A queue API failure must not break the library or any local playback.
            ApplyPlaybackSummary(new PlaybackSummary("Playback unavailable", target.Name, null, null, false, false));
        }
    }

    private void ApplyPlaybackSummary(PlaybackSummary summary)
    {
        FooterTitle.Text = summary.Title;
        FooterArtist.Text = summary.Artist;
        NowPlayingTitle.Text = summary.Title;
        NowPlayingArtist.Text = summary.Artist;

        var state = summary.HasTrack ? (summary.IsPlaying ? "Playing" : "Paused") : "Idle";
        var targetName = SelectedTarget?.Name ?? "No player selected";
        FooterState.Text = $"{state} · {targetName}";
        NowPlayingState.Text = $"{state} · {targetName}";

        var icon = summary.IsPlaying ? "\uE769" : "\uE768";
        FooterPlayPauseIcon.Glyph = icon;
        QueuePlayPauseIcon.Glyph = icon;

        if (SelectedTarget is { IsThisPc: true } && _host.Status.Connection == ConnectionState.Connected)
        {
            var transport = TransportLogic.Describe(_host.Status.Session);
            FooterPlayPauseButton.IsEnabled = QueuePlayPauseButton.IsEnabled = transport.CanPlayPause;
            FooterPreviousButton.IsEnabled = QueuePreviousButton.IsEnabled = transport.CanPrevious;
            FooterNextButton.IsEnabled = QueueNextButton.IsEnabled = transport.CanNext;
        }
        else
        {
            var enabled = SelectedTarget is not null;
            FooterPlayPauseButton.IsEnabled = QueuePlayPauseButton.IsEnabled = enabled;
            FooterPreviousButton.IsEnabled = QueuePreviousButton.IsEnabled = enabled;
            FooterNextButton.IsEnabled = QueueNextButton.IsEnabled = enabled;
        }

        UpdatePlaybackArtwork(summary);
    }

    private void UpdatePlaybackArtwork(PlaybackSummary summary)
    {
        // Avoid decoding the same Sendspin image on every metadata/progress update.
        var key = summary.LocalArtwork is { } local
            ? $"sendspin:{summary.Title}:{local.Version}"
            : $"url:{summary.ArtworkUrl ?? string.Empty}";
        if (key == _artworkCacheKey)
        {
            return;
        }

        _artworkCacheKey = key;
        var request = ++_artworkRequestId;
        if (summary.LocalArtwork is { } artwork)
        {
            FooterArtwork.Source = null;
            NowPlayingArtwork.Source = null;
            _ = LoadLocalArtworkAsync(artwork, request);
        }
        else
        {
            var image = string.IsNullOrWhiteSpace(summary.ArtworkUrl)
                ? null
                : new ListRow(summary.Title, summary.Artist, summary.ArtworkUrl).Image;
            FooterArtwork.Source = image;
            NowPlayingArtwork.Source = image;
        }
    }

    private async Task LoadLocalArtworkAsync(ArtworkImage artwork, int request)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(artwork.Data.AsBuffer());
            stream.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = 400 };
            await image.SetSourceAsync(stream);

            if (!_closed && request == _artworkRequestId && SelectedTarget?.IsThisPc == true)
            {
                FooterArtwork.Source = image;
                NowPlayingArtwork.Source = image;
            }
        }
        catch (Exception)
        {
            if (!_closed && request == _artworkRequestId)
            {
                FooterArtwork.Source = null;
                NowPlayingArtwork.Source = null;
            }
        }
    }

    private void OnServerEvent(MaEvent e)
    {
        // ServerEvent is raised by the Music Assistant WebSocket thread.
        // Never access TargetBox.SelectedItem (or any WinUI control) until we're on the UI thread.
        // Elapsed-time ticks do not change the queue list.
        if (e.Event is not ("queue_updated" or "queue_items_updated"))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            // Recheck after dispatch: the user may have changed targets or closed this window.
            if (_closed || SelectedTarget?.QueueId != e.ObjectId)
            {
                return;
            }

            _queueRefresh.Stop();
            _queueRefresh.Start();
        });
    }

    private void ShowError(MaException ex)
    {
        Message.Severity = InfoBarSeverity.Error;
        Message.Title = ex is MaAuthException ? "Music Assistant needs a token" : "Music Assistant";
        Message.Message = ex.Message;
        SetupButton.Visibility = ex is MaAuthException || _controller.Service.State == MaConnectionState.NeedsSetup ? Visibility.Visible : Visibility.Collapsed;
        Message.IsOpen = true;
    }

    private void ShowInfo(string text)
    {
        Message.Severity = InfoBarSeverity.Success;
        Message.Title = string.Empty;
        Message.Message = text;
        SetupButton.Visibility = Visibility.Collapsed;
        Message.IsOpen = true;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => _openSettings();
}
