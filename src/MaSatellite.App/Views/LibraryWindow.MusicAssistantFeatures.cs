using System.Collections.Generic;
using System.Linq;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MaSatellite.App.Views;

/// <summary>
/// Contextual Music Assistant actions surfaced from the player selector, Now Playing,
/// the search page and playlist library instead of a single feature/developer window.
/// </summary>
public sealed partial class LibraryWindow
{
    private IReadOnlyList<string> _providerFilterIds = [];

    private async Task OpenGroupPlayersAsync()
    {
        try
        {
            var client = await _controller.Service.GetClientAsync();
            var players = (await client.GetPlayersAsync()).Where(p => p.Available).ToList();
            if (players.Count < 2)
            {
                ShowInfo("At least two available Music Assistant players are needed to create a group.");
                return;
            }

            var leader = new ComboBox
            {
                Header = "Group leader",
                DisplayMemberPath = nameof(MaPlayer.Name),
                ItemsSource = players,
                SelectedItem = players.FirstOrDefault(p => p.PlayerId == SelectedTarget?.QueueId) ?? players[0],
                MinWidth = 340
            };
            var members = new ListView
            {
                Header = "Speakers to add",
                DisplayMemberPath = nameof(MaPlayer.Name),
                SelectionMode = ListViewSelectionMode.Multiple,
                Height = 190,
                MinWidth = 340
            };
            void RefreshMembers()
            {
                var currentLeader = leader.SelectedItem as MaPlayer;
                members.ItemsSource = players.Where(p => p.PlayerId != currentLeader?.PlayerId).ToList();
            }
            leader.SelectionChanged += (_, _) => RefreshMembers();
            RefreshMembers();

            var content = new StackPanel { Spacing = 10, MinWidth = 360 };
            content.Children.Add(new TextBlock
            {
                Text = "Choose the player that will lead the synchronized group, then select the speakers to add.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82
            });
            content.Children.Add(leader);
            content.Children.Add(members);

            var dialog = FeatureDialog("Group speakers", content, "Create group");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (leader.SelectedItem is not MaPlayer selectedLeader)
                throw new MaException("Choose a group leader.");
            var selectedMembers = members.SelectedItems.Cast<MaPlayer>()
                .Select(p => p.PlayerId).Distinct().ToList();
            if (selectedMembers.Count == 0)
                throw new MaException("Select at least one speaker to add.");

            await client.SetGroupMembersAsync(selectedLeader.PlayerId, selectedMembers);
            await LoadTargetsAsync();
            ShowInfo($"Asked Music Assistant to group {selectedMembers.Count} speaker(s) with {selectedLeader.Name}.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private async Task UngroupCurrentPlayerAsync()
    {
        if (SelectedTarget is not { } target)
            return;

        try
        {
            await (await _controller.Service.GetClientAsync()).UngroupPlayerAsync(target.QueueId);
            await LoadTargetsAsync();
            ShowInfo($"Ungroup command sent for {target.Name}.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private async Task OpenQueueSettingsAsync()
    {
        if (SelectedTarget is not { } target)
        {
            ShowInfo("Choose a Music Assistant player first.");
            return;
        }

        try
        {
            var client = await _controller.Service.GetClientAsync();
            var queues = await client.GetQueuesAsync();
            var queue = queues.FirstOrDefault(q => q.QueueId == target.QueueId);
            var playlists = await client.GetLibraryAsync(MaMediaType.Playlist, limit: 250, sortField: "name");

            var autoplay = new CheckBox { Content = "Autoplay", IsChecked = queue?.Autoplay ?? false };
            var smartShuffle = new CheckBox { Content = "Smart Shuffle", IsChecked = queue?.SmartShuffleActive ?? false };
            var crossfade = new CheckBox { Content = "Crossfade", IsChecked = queue?.CrossfadeEnabled ?? false };
            var autoplayMode = ChoiceBox(
                ("Automatic", "auto"),
                ("Similar tracks", "similar"),
                ("Similar artists", "similar_artists"),
                ("Library mix", "library"),
                ("Selected playlist", "playlist"));
            autoplayMode.SelectedIndex = 0;
            var playlist = new ComboBox
            {
                Header = "Playlist for Autoplay",
                DisplayMemberPath = nameof(MaItem.Name),
                ItemsSource = playlists,
                MinWidth = 340,
                Visibility = Visibility.Collapsed
            };
            var crossfadeMode = ChoiceBox(
                ("Smart Crossfade", "smart_crossfade"),
                ("Standard Crossfade", "standard_crossfade"));
            crossfadeMode.SelectedIndex = queue?.SmartFadesActive == false ? 1 : 0;

            var autoplayModeRow = new StackPanel { Spacing = 5 };
            autoplayModeRow.Children.Add(new TextBlock { Text = "Continue with", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            autoplayModeRow.Children.Add(autoplayMode);
            autoplayMode.SelectionChanged += (_, _) =>
                playlist.Visibility = SelectedTag(autoplayMode) == "playlist" ? Visibility.Visible : Visibility.Collapsed;

            var content = new StackPanel { Spacing = 12, MinWidth = 360 };
            content.Children.Add(new TextBlock
            {
                Text = $"Queue settings for {target.Name}. Music Assistant applies these settings on the server.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82
            });
            content.Children.Add(autoplay);
            content.Children.Add(autoplayModeRow);
            content.Children.Add(playlist);
            content.Children.Add(smartShuffle);
            content.Children.Add(new TextBlock
            {
                Text = "Smart Shuffle only changes the queue while Shuffle is enabled.",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(crossfade);
            content.Children.Add(new TextBlock { Text = "Transition mode", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(crossfadeMode);

            var dialog = FeatureDialog("Queue settings", content, "Save settings");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            var mode = SelectedTag(autoplayMode) ?? "auto";
            var values = new Dictionary<string, object?>
            {
                ["smart_shuffle_enabled"] = smartShuffle.IsChecked == true ? "enabled" : "disabled",
                ["crossfade_mode"] = SelectedTag(crossfadeMode) ?? "smart_crossfade",
                ["autoplay_mode"] = mode
            };
            if (mode == "playlist")
            {
                if (playlist.SelectedItem is not MaItem selectedPlaylist)
                    throw new MaException("Choose the playlist to use for Autoplay.");
                values["autoplay_playlist"] = selectedPlaylist.Uri;
            }

            await client.SaveQueueSettingsAsync(target.QueueId, values);
            await client.SetQueueAutoplayAsync(target.QueueId, autoplay.IsChecked == true);
            await client.SetQueueCrossfadeAsync(target.QueueId, crossfade.IsChecked == true);
            ShowInfo($"Queue settings saved for {target.Name}.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private void ShowSleepTimerFlyout(FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        foreach (var minutes in new[] { 15, 30, 45, 60 })
            flyout.Items.Add(MenuItem($"{minutes} minutes", () => SetSleepTimerAsync(minutes)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(MenuItem("At the end of this item", () => SetSleepTimerAsync(null, "end_of_item")));
        flyout.Items.Add(MenuItem("At the end of this chapter", () => SetSleepTimerAsync(null, "end_of_chapter")));
        flyout.Items.Add(MenuItem("Custom duration…", OpenCustomSleepTimerAsync));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(MenuItem("Clear sleep timer", ClearSleepTimerAsync));
        flyout.ShowAt(anchor);
    }

    private async Task OpenCustomSleepTimerAsync()
    {
        var minutes = new TextBox
        {
            Header = "Minutes",
            Text = "30",
            PlaceholderText = "1–1440",
            Width = 180
        };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "Choose when Music Assistant should stop playback." });
        content.Children.Add(minutes);
        var dialog = FeatureDialog("Custom sleep timer", content, "Start timer");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (!int.TryParse(minutes.Text, out var value) || value < 1 || value > 1440)
        {
            ShowInfo("Enter a sleep timer between 1 and 1440 minutes.");
            return;
        }
        await SetSleepTimerAsync(value);
    }

    private async Task SetSleepTimerAsync(int? minutes, string? mode = null)
    {
        if (SelectedTarget is not { } target)
        {
            ShowInfo("Choose a player before setting a sleep timer.");
            return;
        }

        try
        {
            await (await _controller.Service.GetClientAsync()).SetSleepTimerAsync(
                target.QueueId, minutes.HasValue ? minutes.Value * 60 : null, mode);
            ShowInfo(mode is null
                ? $"Sleep timer set for {minutes} minutes on {target.Name}."
                : $"Sleep timer set to stop at {mode.Replace('_', ' ')} on {target.Name}.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private async Task ClearSleepTimerAsync()
    {
        if (SelectedTarget is not { } target)
            return;
        try
        {
            await (await _controller.Service.GetClientAsync()).ClearSleepTimerAsync(target.QueueId);
            ShowInfo($"Sleep timer cleared for {target.Name}.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private async Task OpenPlaybackSpeedDialogAsync()
    {
        if (SelectedTarget is not { } target)
        {
            ShowInfo("Choose a player before changing playback speed.");
            return;
        }

        var speed = ChoiceBox(
            ("0.5×", "0.5"), ("0.75×", "0.75"), ("1.0×", "1.0"),
            ("1.25×", "1.25"), ("1.5×", "1.5"), ("1.75×", "1.75"),
            ("2.0×", "2.0"), ("2.5×", "2.5"), ("3.0×", "3.0"));
        speed.SelectedIndex = 2;
        var content = new StackPanel { Spacing = 8, MinWidth = 320 };
        content.Children.Add(new TextBlock
        {
            Text = "Variable playback speed is supported by Music Assistant for audiobooks and podcast episodes.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.82
        });
        content.Children.Add(speed);

        var dialog = FeatureDialog("Playback speed", content, "Apply");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (!double.TryParse(SelectedTag(speed), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var selectedSpeed))
            throw new MaException("Choose a playback speed.");

        try
        {
            await (await _controller.Service.GetClientAsync()).SetPlaybackSpeedAsync(target.QueueId, selectedSpeed);
            NowPage.SetPlaybackSpeed(selectedSpeed);
            ShowInfo($"Requested {selectedSpeed:0.##}× speed on {target.Name}.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private async void OnProviderFilterClick(object sender, RoutedEventArgs e)
    {
        var providers = new TextBox
        {
            Header = "Provider instance IDs",
            Text = string.Join(", ", _providerFilterIds),
            PlaceholderText = "e.g. soundcloud, spotify",
            AcceptsReturn = false,
            Width = 380
        };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Leave empty to search every configured provider. Separate provider instance IDs with commas.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.82
        });
        content.Children.Add(providers);
        var dialog = FeatureDialog("Search providers", content, "Apply filter");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _providerFilterIds = (providers.Text ?? string.Empty)
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ProviderFilterButton.Content = _providerFilterIds.Count == 0
            ? "Providers"
            : $"Providers ({_providerFilterIds.Count})";
        if (_tab == "search")
            await NavigateAsync("search", forceReload: true);
    }

    private async void OnCreatePlaylist(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "Playlist name", PlaceholderText = "My playlist", Width = 360 };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "Create a new playlist in your Music Assistant library." });
        content.Children.Add(name);
        var dialog = FeatureDialog("New playlist", content, "Create");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (string.IsNullOrWhiteSpace(name.Text))
        {
            ShowInfo("Enter a name for the playlist.");
            return;
        }

        try
        {
            var created = await (await _controller.Service.GetClientAsync()).CreatePlaylistAsync(name.Text.Trim());
            await NavigateAsync("playlist", forceReload: true);
            ShowInfo(created is null ? "Playlist creation submitted." : $"Created playlist: {created.Name}.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private async void OnImportPlaylist(object sender, RoutedEventArgs e)
    {
        var data = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 240,
            MinWidth = 430,
            PlaceholderText = "Paste an M3U or M3U8 playlist here…"
        };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "Paste playlist text to import it into Music Assistant." });
        content.Children.Add(data);
        var dialog = FeatureDialog("Import playlist", content, "Import");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (string.IsNullOrWhiteSpace(data.Text))
        {
            ShowInfo("Paste M3U or M3U8 content to import.");
            return;
        }

        try
        {
            var imported = await (await _controller.Service.GetClientAsync()).ImportPlaylistAsync(data.Text);
            await NavigateAsync("playlist", forceReload: true);
            ShowInfo(imported is null ? "Playlist import submitted." : $"Imported playlist: {imported.Name}.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private async Task AddTrackToPlaylistAsync(MaItem track)
    {
        try
        {
            var client = await _controller.Service.GetClientAsync();
            var playlists = await client.GetLibraryAsync(MaMediaType.Playlist, limit: 250, sortField: "name");
            if (playlists.Count == 0)
            {
                ShowInfo("Create a playlist before adding tracks to it.");
                return;
            }

            var picker = new ComboBox
            {
                Header = "Destination playlist",
                DisplayMemberPath = nameof(MaItem.Name),
                ItemsSource = playlists,
                SelectedIndex = 0,
                MinWidth = 340
            };
            var content = new StackPanel { Spacing = 8, MinWidth = 360 };
            content.Children.Add(new TextBlock
            {
                Text = $"Add “{track.Name}” to one of your Music Assistant playlists.",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(picker);
            var dialog = FeatureDialog("Add to playlist", content, "Add track");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            if (picker.SelectedItem is not MaItem playlist)
                throw new MaException("Choose a destination playlist.");

            await client.AddToPlaylistAsync(playlist.ItemId, [track.Uri]);
            ShowInfo($"Added {track.Name} to {playlist.Name}.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private async Task ExportPlaylistToClipboardAsync(MaItem playlist)
    {
        try
        {
            var data = await (await _controller.Service.GetClientAsync()).ExportPlaylistAsync(playlist);
            var package = new DataPackage();
            package.SetText(data);
            Clipboard.SetContent(package);
            ShowInfo($"Copied {playlist.Name} as M3U8 to the clipboard.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private async Task RemoveFromPlaylistAsync(MaItem playlist, MaItem track)
    {
        try
        {
            var positions = _currentLibraryRows
                .Select((row, index) => (row, index))
                .Where(pair => ReferenceEquals(pair.row.Item, track))
                .Select(pair => pair.index)
                .ToList();
            if (positions.Count != 1)
                throw new MaException("The selected track could not be matched safely to one playlist position.");

            await (await _controller.Service.GetClientAsync()).RemovePlaylistTracksAsync(
                playlist, positions, [track.Uri]);
            await GoToAsync(new NavState(_tab, playlist, QueryFor(_tab)), forceReload: true);
            ShowInfo($"Removed {track.Name} from {playlist.Name}.");
        }
        catch (Exception ex)
        {
            ShowFeatureError(ex);
        }
    }

    private static ComboBox ChoiceBox(params (string Label, string Tag)[] choices)
    {
        var combo = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (label, tag) in choices)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        return combo;
    }

    private static string? SelectedTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private ContentDialog FeatureDialog(string title, UIElement content, string primary) => new()
    {
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
        XamlRoot = LibraryRoot.XamlRoot,
        RequestedTheme = ElementTheme.Dark
    };

    private void ShowFeatureError(Exception ex) =>
        ShowInfo(ex is MaException ? ex.Message : $"Music Assistant operation failed: {ex.Message}");
}
