using MaSatellite.Host.Presentation;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace MaSatellite.App.Views;

// The Now Playing page: artwork and track details, with Up next / Lyrics / Related beside them.
// It reads the same playback state as the playback bar (ApplyPlaybackSummary); it keeps none of its own.
public sealed partial class LibraryWindow
{
    private int _queueItemCount;

    // ---- Navigation ----

    private async void OnNavigateNowPlaying(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key })
        {
            return;
        }

        // The playback bar buttons toggle: pressing the one that opened the page closes it again.
        if (_tab == "now" && _nowNavKey == key && _nav.CanGoBack)
        {
            await GoBackAsync();
            return;
        }

        await GoToAsync(new NavState("now", null, key));
    }

    private async Task OpenNowPlayingTabAsync(string tab)
    {
        if (_tab == "now")
        {
            SelectNowTab(tab);
            return;
        }

        _nowTab = tab;
        await GoToAsync(new NavState("now", null, "now"));
    }

    /// <summary>Shows or hides the Now Playing page in place of the page header and body.</summary>
    private void ShowNowPage(bool show)
    {
        var wasShown = NowScroll.Visibility == Visibility.Visible;
        NowScroll.Visibility = NowBackdrop.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PageHeader.Visibility = Body.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show && !wasShown)
        {
            StartNowGlow();
            Motion.FadeIn(NowScroll, 260);
            Motion.FadeIn(NowBackdrop, 480);
        }
        else if (!show)
        {
            StopNowGlow();
        }
    }

    private async Task EnterNowPageAsync()
    {
        Message.IsOpen = false;
        ShowNowPage(true);
        UpdateNavigationState();
        LayoutNowPage(LibraryRoot.ActualWidth);
        ApplyNowBackdrop();
        _ = EnsurePaletteAsync();
        SelectNowTab(_nowTab);
        await RefreshPlaybackSummaryAsync();
        await LoadQueueAsync();
    }

    // ---- Layout ----

    private void OnNowPageSizeChanged(object sender, SizeChangedEventArgs e) => LayoutNowPage(LibraryRoot.ActualWidth);

    /// <summary>Side by side on wide windows; stacked (details, then tabs) on narrow ones.</summary>
    private void LayoutNowPage(double windowWidth)
    {
        var stacked = windowWidth < 1180;
        if (stacked != _nowStacked)
        {
            _nowStacked = stacked;
            NowPage.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1.25, GridUnitType.Star);
            NowPage.ColumnSpacing = stacked ? 0 : 44;
            Grid.SetColumnSpan(NowDetails, stacked ? 2 : 1);
            Grid.SetRowSpan(NowDetails, stacked ? 1 : 2);
            Grid.SetRow(NowTabsPanel, stacked ? 1 : 0);
            Grid.SetColumn(NowTabsPanel, stacked ? 0 : 1);
            Grid.SetColumnSpan(NowTabsPanel, stacked ? 2 : 1);
            Grid.SetRowSpan(NowTabsPanel, stacked ? 1 : 2);
        }

        // Artwork is the hero: as large as the column and the window height allow.
        var width = Math.Max(0, NowScroll.ActualWidth - 24);
        var column = stacked ? width : Math.Max(300, (width - 44) / 2.25);
        var byHeight = stacked ? 420 : Math.Max(200, NowScroll.ActualHeight - 260);
        var size = Math.Clamp(Math.Min(column, byHeight), 200, 480);
        NowPlayingArtworkFrame.Width = NowPlayingArtworkFrame.Height = size;
    }

    // ---- Tabs ----

    private void OnNowTab(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tab })
        {
            SelectNowTab(tab);
        }
    }

    private void SelectNowTab(string tab)
    {
        _nowTab = tab;
        TabUpNext.IsChecked = tab == "upnext";
        TabLyrics.IsChecked = tab == "lyrics";
        TabRelated.IsChecked = tab == "related";
        UpNextView.Visibility = tab == "upnext" ? Visibility.Visible : Visibility.Collapsed;
        LyricsView.Visibility = tab == "lyrics" ? Visibility.Visible : Visibility.Collapsed;
        RelatedView.Visibility = tab == "related" ? Visibility.Visible : Visibility.Collapsed;
        QueueActions.Visibility = tab == "upnext" ? Visibility.Visible : Visibility.Collapsed;
        FrameworkElement shown = tab switch { "lyrics" => LyricsView, "related" => RelatedView, _ => UpNextView };
        Motion.FadeIn(shown, 200);
        if (tab == "lyrics")
        {
            UpdateLyrics();
        }
        else if (tab == "related")
        {
            _ = LoadRelatedAsync();
        }
    }

    // ---- Up next ----

    private async Task LoadQueueAsync()
    {
        var id = ++_queueLoadId;
        try
        {
            if (SelectedTarget is not { } target)
            {
                ShowQueueRows([], null, "Choose a player to see its queue.");
                return;
            }

            var view = await _controller.GetQueueAsync(target);
            if (_closed || id != _queueLoadId || _tab != "now")
            {
                return;
            }

            var current = view.Queue?.CurrentIndex;
            var rows = view.Items
                .Select(q => new ListRow(q.Name, q.Subtitle ?? string.Empty, q.ImageUrl, null, q,
                    isCurrent: q.Index == current, position: q.Index + 1))
                .ToList();
            ShowQueueRows(rows, view.Queue?.ItemCount, "The queue is empty.");
        }
        catch (MaException ex)
        {
            if (!_closed && id == _queueLoadId)
            {
                ShowError(ex);
            }
        }
    }

    private void ShowQueueRows(List<ListRow> rows, int? total, string emptyText)
    {
        _queueItemCount = Math.Max(total ?? 0, rows.Count);
        QueueList.ItemsSource = rows.Count > 0 ? rows : null;
        QueueEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueEmptyText.Text = emptyText;
        ClearQueueButton.IsEnabled = rows.Count > 0;
        QueueSubtitle.Text = rows.Count == 0
            ? "Nothing is queued on the selected player."
            : $"{_queueItemCount} {(_queueItemCount == 1 ? "track" : "tracks")} in the queue · {SelectedTarget?.Name}";
    }

    private async void OnQueueRowUp(object sender, RoutedEventArgs e) => await MoveQueueRowAsync(sender, -1);

    private async void OnQueueRowDown(object sender, RoutedEventArgs e) => await MoveQueueRowAsync(sender, 1);

    private async Task MoveQueueRowAsync(object sender, int shift)
    {
        if ((sender as FrameworkElement)?.DataContext is not ListRow { QueueItem: { } entry } || SelectedTarget is not { } target)
        {
            return;
        }

        var destination = entry.Index + shift;
        if (destination < 0 || destination >= _queueItemCount)
        {
            return; // already first or last
        }

        await RunAsync(async () =>
        {
            await _controller.MoveQueueItemAsync(target, entry.QueueItemId, shift);
            await LoadQueueAsync();
        });
    }

    private async void OnQueueRowRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ListRow { QueueItem: { } entry } || SelectedTarget is not { } target)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _controller.RemoveQueueItemAsync(target, entry.QueueItemId);
            ShowInfo($"Removed '{entry.Name}' from the queue.");
            await LoadQueueAsync();
            await RefreshPlaybackSummaryAsync();
        });
    }

    private async void OnClearQueue(object sender, RoutedEventArgs e)
    {
        if (SelectedTarget is not { } target || _queueItemCount == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = LibraryRoot.XamlRoot,
            Title = "Clear the queue?",
            Content = $"This removes all {_queueItemCount} tracks from the queue of {target.Name}.",
            PrimaryButtonText = "Clear queue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _controller.ClearQueueAsync(target);
            ShowInfo($"Cleared the queue of {target.Name}.");
            await LoadQueueAsync();
            await RefreshPlaybackSummaryAsync();
        });
    }

    // ---- The playing track as a library item: favourite, lyrics, album ----

    /// <summary>Looks up the library item behind the playing track whenever the track changes.</summary>
    private void TrackChanged(PlaybackSummary summary)
    {
        var hasTrack = SelectedTarget is not null && summary.Title is not ("Nothing playing" or "Playback unavailable");
        var key = hasTrack ? $"{SelectedTarget!.QueueId}\u0001{summary.Title}\u0001{summary.Artist}" : null;
        if (key == _resolvedKey)
        {
            return;
        }

        _resolvedKey = key;
        if (key is null)
        {
            SetNowItem(null);
            return;
        }

        _ = ResolveCurrentItemAsync(key);
    }

    private async Task ResolveCurrentItemAsync(string key)
    {
        var request = ++_resolveId;
        try
        {
            if (SelectedTarget is not { } target)
            {
                return;
            }

            var entry = await _controller.GetCurrentQueueItemAsync(target);
            if (_closed || request != _resolveId || key != _resolvedKey)
            {
                return;
            }

            if (string.IsNullOrEmpty(entry?.Uri))
            {
                SetNowItem(null);
                return;
            }

            var item = await _controller.GetItemByUriAsync(entry.Uri);
            if (_closed || request != _resolveId || key != _resolvedKey)
            {
                return;
            }

            SetNowItem(item);
        }
        catch (MaException)
        {
            // Without the library item there is no favourite or lyrics: the controls stay disabled.
            if (!_closed && request == _resolveId)
            {
                SetNowItem(null);
            }
        }
    }

    private void SetNowItem(MaItem? item)
    {
        _nowItem = item;
        UpdateFavoriteUi();
        var album = item is null ? null : string.Join(" · ", new[] { item.AlbumName, item.Year?.ToString() }.Where(v => !string.IsNullOrWhiteSpace(v)));
        NowPlayingAlbum.Text = album ?? string.Empty;
        NowPlayingAlbum.Visibility = string.IsNullOrEmpty(album) ? Visibility.Collapsed : Visibility.Visible;
        if (_tab == "now" && _nowTab == "lyrics")
        {
            UpdateLyrics();
        }
        else if (_tab == "now" && _nowTab == "related")
        {
            _ = LoadRelatedAsync();
        }
    }

    private void UpdateFavoriteUi()
    {
        var favorite = _nowItem?.Favorite == true;
        var glyph = favorite ? "\uEB52" : "\uEB51";
        var label = favorite ? "Remove from favourites" : "Add to favourites";
        var brush = (Brush)Application.Current.Resources[favorite ? "AuroraAccent" : "AuroraMuted"];
        foreach (var (button, icon) in new[] { (NowFavoriteButton, NowFavoriteIcon), (FooterFavoriteButton, FooterFavoriteIcon) })
        {
            button.IsEnabled = _nowItem is not null;
            icon.Glyph = glyph;
            icon.Foreground = brush;
            ToolTipService.SetToolTip(button, _nowItem is null ? "Favourites aren't available for this track" : label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        }
    }

    private async void OnToggleFavorite(object sender, RoutedEventArgs e) => await ToggleFavoriteAsync();

    private async Task ToggleFavoriteAsync()
    {
        if (_nowItem is not { } item)
        {
            return;
        }

        var want = !item.Favorite;
        await RunAsync(async () =>
        {
            await _controller.SetFavoriteAsync(item, want);
            if (_nowItem?.Uri == item.Uri)
            {
                _nowItem = item with { Favorite = want };
                UpdateFavoriteUi();
            }

            ShowInfo(want ? $"Added '{item.Name}' to your favourites." : $"Removed '{item.Name}' from your favourites.");
        });
    }

    private void OnNowMore(object sender, RoutedEventArgs e) => ShowTrackMenu((FrameworkElement)sender);

    private void OnFooterMore(object sender, RoutedEventArgs e) => ShowTrackMenu((FrameworkElement)sender);

    private void ShowTrackMenu(FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        var favorite = new MenuFlyoutItem
        {
            Text = _nowItem?.Favorite == true ? "Remove from favourites" : "Add to favourites",
            IsEnabled = _nowItem is not null,
        };
        favorite.Click += async (_, _) => await ToggleFavoriteAsync();
        flyout.Items.Add(favorite);
        flyout.Items.Add(MenuItem("Show up next", () => OpenNowPlayingTabAsync("upnext")));
        flyout.Items.Add(MenuItem("Show lyrics", () => OpenNowPlayingTabAsync("lyrics")));
        flyout.Items.Add(MenuItem("Show related artists", () => OpenNowPlayingTabAsync("related")));
        flyout.Items.Add(new MenuFlyoutSeparator());
        var transfer = new MenuFlyoutItem { Text = "Transfer queue to…" };
        transfer.Click += (_, _) => OnTransferClick(anchor, new RoutedEventArgs());
        flyout.Items.Add(transfer);
        flyout.ShowAt(anchor);
    }

    // ---- Lyrics and related: real data or an honest empty state ----

    private void UpdateLyrics()
    {
        var lyrics = _nowItem?.Lyrics;
        if (!string.IsNullOrWhiteSpace(lyrics))
        {
            LyricsText.Text = LyricsFormatter.ToPlainText(lyrics);
            LyricsScroll.Visibility = Visibility.Visible;
            LyricsEmpty.Visibility = Visibility.Collapsed;
            LyricsScroll.ChangeView(null, 0, null, disableAnimation: true);
            return;
        }

        LyricsScroll.Visibility = Visibility.Collapsed;
        LyricsEmpty.Visibility = Visibility.Visible;
        LyricsEmptyTitle.Text = "No lyrics available";
        LyricsEmptyDetail.Text = _nowItem is null
            ? "Lyrics appear here when a track from your library is playing and its provider supplies them."
            : "Music Assistant has no lyrics for this track. They show up when a provider supplies them.";
    }

    private async Task LoadRelatedAsync()
    {
        var artist = _nowItem is null ? null : _latestPlayback.Artist;
        if (string.IsNullOrWhiteSpace(artist))
        {
            _relatedKey = null;
            ++_relatedId;
            RelatedGrid.ItemsSource = null;
            RelatedBusy.IsActive = false;
            ShowRelatedEmpty("Nothing related yet", "Play a track from your library to see artists similar to it.");
            return;
        }

        if (artist == _relatedKey)
        {
            return;
        }

        var request = ++_relatedId;
        _relatedKey = artist;
        RelatedGrid.ItemsSource = null;
        RelatedEmpty.Visibility = Visibility.Collapsed;
        RelatedBusy.IsActive = true;
        try
        {
            var similar = await _controller.GetRelatedArtistsAsync(artist);
            if (_closed || request != _relatedId)
            {
                return;
            }

            var rows = similar.Select(i => ListRow.From(i)).ToList();
            RelatedGrid.ItemsSource = rows.Count > 0 ? rows : null;
            if (rows.Count == 0)
            {
                ShowRelatedEmpty("No related artists", $"Music Assistant found no artists similar to {RelatedArtist.PrimaryName(artist)}. Not every provider offers this.");
            }
        }
        catch (MaException)
        {
            if (!_closed && request == _relatedId)
            {
                _relatedKey = null; // let the next visit try again
                ShowRelatedEmpty("Related artists aren't available", "Your Music Assistant server couldn't provide similar artists right now.");
            }
        }
        finally
        {
            if (request == _relatedId)
            {
                RelatedBusy.IsActive = false;
            }
        }
    }

    private void ShowRelatedEmpty(string title, string detail)
    {
        RelatedEmptyTitle.Text = title;
        RelatedEmptyDetail.Text = detail;
        RelatedEmpty.Visibility = Visibility.Visible;
    }

    private async void OnRelatedClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ListRow { Item: { } artist })
        {
            await GoToAsync(new NavState("artist", artist));
        }
    }

    // ---- Atmosphere ----

    /// <summary>Tints the page backdrop with a dark tone of the artwork's colour; the default Aurora gradient otherwise.</summary>
    private void ApplyNowBackdrop()
    {
        NowTint.Background = _tintedBrush ?? (Brush)Application.Current.Resources["AuroraHeroGradient"];
        if (NowBackdrop.Visibility == Visibility.Visible)
        {
            Motion.FadeIn(NowTint, 600);
        }
    }

    private void StartNowGlow() => Motion.Pulse(NowGlow, 7000);

    private void StopNowGlow()
    {
        try
        {
            ElementCompositionPreview.GetElementVisual(NowGlow).StopAnimation("Opacity");
            NowGlow.Opacity = 1;
        }
        catch (Exception)
        {
            // Only the decorative glow is affected.
        }
    }
}
