using MaSatellite.Host.Library;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace MaSatellite.App.Views;

/// <summary>Browse the Music Assistant library, search, play on any player and manage queues.</summary>
public sealed partial class LibraryWindow : Window
{
    private readonly LibraryController _controller;
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

    internal LibraryWindow(LibraryController controller, Action openSettings)
    {
        _controller = controller;
        _openSettings = openSettings;
        InitializeComponent();
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

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab })
        {
            var item = Tabs.Items.FirstOrDefault(i => (string?)i.Tag == tab);
            if (item is not null)
            {
                // Tab change runs through the existing navigation and data loading logic.
                if (Tabs.SelectedItem == item && _openedItem is not null)
                {
                    _openedItem = null;
                    _history.Clear();
                    _ = LoadTabAsync();
                }
                else
                {
                    Tabs.SelectedItem = item;
                }
            }
        }
    }

    private async void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string tab || (tab == _tab && _openedItem is null))
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
        _tab = "search";
        _openedItem = null;
        _history.Clear();
        Tabs.SelectedItem = Tabs.Items.First(i => (string?)i.Tag == "search");
        await LoadTabAsync();
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
        foreach (var button in new[] { NavHome, NavLibrary, NavNow, NavSearch, NavQueue, NavArtist, NavPlaylist, NavTrack })
        {
            button.Style = (string?)button.Tag == _tab ? selected : regular;
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
        if (SelectedTarget is { } target)
        {
            await RunAsync(() => _controller.QueueControlAsync(target, command));
            await RefreshPlaybackSummaryAsync();
        }
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

    private async Task RefreshPlaybackSummaryAsync()
    {
        if (_closed)
        {
            return;
        }

        if (SelectedTarget is not { } target)
        {
            FooterTitle.Text = "Nothing playing";
            FooterArtist.Text = "Choose a player";
            NowPlayingTitle.Text = "Nothing playing";
            NowPlayingArtist.Text = "Choose a player";
            FooterArtwork.Source = null;
            NowPlayingArtwork.Source = null;
            return;
        }

        try
        {
            var view = await _controller.GetQueueAsync(target);
            if (_closed || SelectedTarget?.QueueId != target.QueueId)
            {
                return;
            }

            var currentIndex = view.Queue?.CurrentIndex;
            var current = view.Items.FirstOrDefault(q => q.Index == currentIndex);
            var title = current?.Name ?? "Nothing playing";
            var artist = current?.Subtitle ?? target.Name;
            FooterTitle.Text = title;
            FooterArtist.Text = artist;
            NowPlayingTitle.Text = title;
            NowPlayingArtist.Text = artist;
            var artwork = current is null ? null : new ListRow(title, artist, current.ImageUrl).Image;
            FooterArtwork.Source = artwork;
            NowPlayingArtwork.Source = artwork;
        }
        catch (MaException)
        {
            // Queue API failures must not blank an otherwise working library.
            FooterTitle.Text = "Playback unavailable";
            FooterArtist.Text = target.Name;
            NowPlayingTitle.Text = "Playback unavailable";
            NowPlayingArtist.Text = target.Name;
            FooterArtwork.Source = null;
            NowPlayingArtwork.Source = null;
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
