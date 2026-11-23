using System.Diagnostics;
using System.Linq;
using MaSatellite.Host.Library;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MaSatellite.App.Views;

/// <summary>
/// Native companion controls for Music Assistant capabilities that are not part of the
/// everyday library/player surface. All playback and library operations remain server-owned.
/// </summary>
public sealed class AdvancedFeaturesWindow : Window
{
    private readonly MaLibraryService _library;
    private readonly TextBlock _status = new() { Text = "Connect to Music Assistant to load players and media." };

    private readonly ComboBox _groupLeader = new() { Width = 280, PlaceholderText = "Choose a group leader" };
    private readonly ListView _groupMembers = new() { Height = 126, SelectionMode = ListViewSelectionMode.Multiple, DisplayMemberPath = nameof(MaPlayer.Name) };
    private readonly ComboBox _ungroupPlayer = new() { Width = 280, PlaceholderText = "Choose a player to ungroup" };

    private readonly ComboBox _queuePlayer = new() { Width = 280, PlaceholderText = "Choose a player" };
    private readonly CheckBox _autoplay = new() { Content = "Autoplay" };
    private readonly CheckBox _smartShuffle = new() { Content = "Smart Shuffle" };
    private readonly CheckBox _crossfade = new() { Content = "Crossfade" };
    private readonly ComboBox _crossfadeMode = new() { Width = 220, PlaceholderText = "Crossfade mode" };

    private readonly TextBox _sleepMinutes = new() { Width = 100, Text = "30", PlaceholderText = "Minutes" };

    private readonly ComboBox _mediaKind = new() { Width = 220 };
    private readonly ListView _mediaItems = new() { Height = 190, DisplayMemberPath = nameof(MaItem.Name) };
    private readonly TextBlock _mediaDetails = new() { TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Opacity = 0.8 };

    private readonly TextBox _searchQuery = new() { Width = 260, PlaceholderText = "Search tracks, podcasts, audiobooks…" };
    private readonly TextBox _searchProviders = new() { Width = 230, PlaceholderText = "Provider IDs (comma separated)" };
    private readonly ListView _searchResults = new() { Height = 180, DisplayMemberPath = nameof(MaItem.Name) };

    private readonly ComboBox _playlistPicker = new() { Width = 280, PlaceholderText = "Choose a playlist" };
    private readonly ListView _playlistTracks = new() { Height = 150, SelectionMode = ListViewSelectionMode.Multiple, DisplayMemberPath = nameof(MaItem.Name) };
    private readonly TextBox _playlistName = new() { Width = 260, PlaceholderText = "New playlist name" };
    private readonly TextBox _m3uData = new()
    {
        AcceptsReturn = true,
        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        Height = 115,
        PlaceholderText = "Paste M3U / M3U8 content here to import, or export a selected playlist here."
    };

    private IReadOnlyList<MaPlayer> _players = [];
    private IReadOnlyList<MaItem> _currentMedia = [];
    private IReadOnlyList<MaItem> _currentSearch = [];
    private IReadOnlyList<MaItem> _currentPlaylistTracks = [];
    private MaItem? _showingPodcast;
    private string? _serverUrl;
    private bool _loading;

    public AdvancedFeaturesWindow(MaLibraryService library)
    {
        _library = library;
        Title = "MA-Satellite · Music Assistant features";
        Content = BuildContent();
        Activated += OnActivated;
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { Padding = new Thickness(24), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = "Music Assistant features",
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "Multi-room playback, queue intelligence, spoken-word libraries and playlist tools — powered by your Music Assistant server.",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            Opacity = 0.8
        });

        var refresh = new Button { Content = "Refresh Music Assistant data" };
        refresh.Click += async (_, _) => await RefreshAsync();
        root.Children.Add(refresh);

        var groups = Section("Multi-room groups", "Create a temporary synchronized group using a player that supports grouping. Music Assistant validates which combinations are compatible.");
        var groupRow = Horizontal();
        groupRow.Children.Add(_groupLeader);
        groupRow.Children.Add(Button("Group selected", async () => await GroupSelectedAsync()));
        groupRow.Children.Add(Button("Refresh players", async () => await RefreshPlayersAsync()));
        groups.Children.Add(groupRow);
        groups.Children.Add(new TextBlock { Text = "Choose the leader, then select the players to add:", Opacity = 0.8 });
        groups.Children.Add(_groupMembers);
        var ungroupRow = Horizontal();
        ungroupRow.Children.Add(_ungroupPlayer);
        ungroupRow.Children.Add(Button("Ungroup player", async () => await UngroupSelectedAsync()));
        groups.Children.Add(ungroupRow);
        root.Children.Add(groups);

        var playback = Section("Intelligent playback", "These switches change the selected player's Music Assistant queue settings. Smart Shuffle works together with Shuffle; Autoplay and Crossfade are managed by the server.");
        var queueRow = Horizontal();
        queueRow.Children.Add(_queuePlayer);
        queueRow.Children.Add(Button("Load queue state", async () => await LoadQueueSettingsAsync()));
        playback.Children.Add(queueRow);
        var checks = Horizontal();
        checks.Children.Add(_autoplay);
        checks.Children.Add(_smartShuffle);
        checks.Children.Add(_crossfade);
        playback.Children.Add(checks);
        var crossfadeRow = Horizontal();
        _crossfadeMode.Items.Add(new ComboBoxItem { Content = "Smart Crossfade", Tag = "smart_crossfade" });
        _crossfadeMode.Items.Add(new ComboBoxItem { Content = "Standard Crossfade", Tag = "standard_crossfade" });
        crossfadeRow.Children.Add(new TextBlock { Text = "Transition mode", VerticalAlignment = VerticalAlignment.Center });
        crossfadeRow.Children.Add(_crossfadeMode);
        crossfadeRow.Children.Add(Button("Apply queue settings", async () => await ApplyQueueSettingsAsync()));
        playback.Children.Add(crossfadeRow);
        root.Children.Add(playback);

        var sleep = Section("Sleep timer", "Stop after a duration, the current item, or the current chapter. Chapter timing depends on chapter metadata from the provider.");
        var sleepRow = Horizontal();
        sleepRow.Children.Add(new TextBlock { Text = "Stop after (minutes)", VerticalAlignment = VerticalAlignment.Center });
        sleepRow.Children.Add(_sleepMinutes);
        sleepRow.Children.Add(Button("Start timer", async () => await SetSleepTimerAsync()));
        sleepRow.Children.Add(Button("End of item", async () => await SetSleepTimerAsync("end_of_item")));
        sleepRow.Children.Add(Button("End of chapter", async () => await SetSleepTimerAsync("end_of_chapter")));
        sleepRow.Children.Add(Button("Clear timer", async () => await ClearSleepTimerAsync()));
        sleep.Children.Add(sleepRow);
        root.Children.Add(sleep);

        var library = Section("Podcasts, audiobooks and genres", "Browse the additional Music Assistant media types. In-progress items include supported resume positions; choosing Play sends the original Music Assistant item URI back to the server.");
        var mediaRow = Horizontal();
        AddMediaKind("Audiobooks", "audiobooks");
        AddMediaKind("Podcasts", "podcasts");
        AddMediaKind("Genres", "genres");
        AddMediaKind("In progress", "in_progress");
        mediaRow.Children.Add(_mediaKind);
        mediaRow.Children.Add(Button("Load library", async () => await LoadMediaAsync()));
        mediaRow.Children.Add(Button("Load podcast episodes", async () => await LoadPodcastEpisodesAsync()));
        mediaRow.Children.Add(Button("Play selected", async () => await PlaySelectedMediaAsync()));
        library.Children.Add(mediaRow);
        library.Children.Add(_mediaItems);
        library.Children.Add(_mediaDetails);
        root.Children.Add(library);

        var search = Section("Provider-aware search", "Search the additional media types and optionally restrict the search to provider instance IDs. Leave the provider filter empty to search all configured sources.");
        var searchRow = Horizontal();
        searchRow.Children.Add(_searchQuery);
        searchRow.Children.Add(_searchProviders);
        searchRow.Children.Add(Button("Search", async () => await SearchAsync()));
        searchRow.Children.Add(Button("Play result", async () => await PlaySelectedSearchAsync()));
        search.Children.Add(searchRow);
        search.Children.Add(_searchResults);
        root.Children.Add(search);

        var playlists = Section("Playlist management", "Create playlists, add a selected search result, remove selected entries, and import/export M3U or M3U8 data.");
        var createRow = Horizontal();
        createRow.Children.Add(_playlistName);
        createRow.Children.Add(Button("Create playlist", async () => await CreatePlaylistAsync()));
        createRow.Children.Add(_playlistPicker);
        createRow.Children.Add(Button("Refresh playlists", async () => await LoadPlaylistsAsync()));
        playlists.Children.Add(createRow);
        var playlistActions = Horizontal();
        playlistActions.Children.Add(Button("Load playlist tracks", async () => await LoadPlaylistTracksAsync()));
        playlistActions.Children.Add(Button("Add search result", async () => await AddSearchResultToPlaylistAsync()));
        playlistActions.Children.Add(Button("Remove selected tracks", async () => await RemovePlaylistTracksAsync()));
        playlistActions.Children.Add(Button("Export M3U8", async () => await ExportPlaylistAsync()));
        playlists.Children.Add(playlistActions);
        playlists.Children.Add(_playlistTracks);
        var importRow = Horizontal();
        importRow.Children.Add(Button("Import M3U / M3U8", async () => await ImportPlaylistAsync()));
        importRow.Children.Add(Button("Copy M3U8 to clipboard", CopyPlaylistExport));
        playlists.Children.Add(importRow);
        playlists.Children.Add(_m3uData);
        root.Children.Add(playlists);

        var plugins = Section("Music Assistant plugins", "Plugin logic remains on the Music Assistant server. Enable the relevant plugins there, then open the server UI to configure and use them. No duplicate plugin implementation is bundled into Satellite.");
        plugins.Children.Add(new TextBlock
        {
            Text = "Available integrations: AI Radio · Party Mode · Music Quiz · Smart Playlists · Library Recommendations · MilkDrop Visualizer",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
        });
        plugins.Children.Add(Button("Open Music Assistant", OpenMusicAssistant));
        root.Children.Add(plugins);

        root.Children.Add(_status);
        var scroll = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        return scroll;
    }

    private void AddMediaKind(string label, string tag)
    {
        _mediaKind.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        if (_mediaKind.SelectedIndex < 0) _mediaKind.SelectedIndex = 0;
    }

    private static StackPanel Section(string title, string description)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 16, 0, 8) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Opacity = 0.75 });
        return panel;
    }

    private static StackPanel Horizontal() => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button Button(string label, Func<Task> action)
    {
        var button = new Button { Content = label, MinHeight = 34 };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, MinHeight = 34 };
        button.Click += (_, _) => action();
        return button;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            await RefreshPlayersAsync();
            await LoadPlaylistsAsync();
            _status.Text = $"Connected to Music Assistant{(_serverUrl is null ? string.Empty : $" at {_serverUrl}")}.";
        }
        catch (Exception ex)
        {
            SetStatus(ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RefreshPlayersAsync()
    {
        var client = await _library.GetClientAsync();
        _serverUrl = client.BaseUrl;
        _players = await client.GetPlayersAsync();
        var available = _players.Where(p => p.Available).ToList();
        _groupLeader.ItemsSource = available;
        _ungroupPlayer.ItemsSource = available;
        _queuePlayer.ItemsSource = available;
        if (_groupLeader.SelectedIndex < 0 && available.Count > 0) _groupLeader.SelectedIndex = 0;
        if (_ungroupPlayer.SelectedIndex < 0 && available.Count > 0) _ungroupPlayer.SelectedIndex = 0;
        if (_queuePlayer.SelectedIndex < 0 && available.Count > 0) _queuePlayer.SelectedIndex = 0;
        var leader = _groupLeader.SelectedItem as MaPlayer;
        _groupMembers.ItemsSource = available.Where(p => p.PlayerId != leader?.PlayerId).ToList();
        _status.Text = $"{available.Count} available player(s) found.";
    }

    private async Task GroupSelectedAsync()
    {
        try
        {
            if (_groupLeader.SelectedItem is not MaPlayer leader)
                throw new MaException("Choose a group leader first.");
            var members = _groupMembers.SelectedItems.Cast<MaPlayer>().Select(p => p.PlayerId).Distinct().ToList();
            if (members.Count == 0) throw new MaException("Select at least one player to add to the group.");
            await (await _library.GetClientAsync()).SetGroupMembersAsync(leader.PlayerId, members);
            await RefreshPlayersAsync();
            _status.Text = $"Asked Music Assistant to add {members.Count} player(s) to {leader.Name}. The server/provider decides whether the combination is supported.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task UngroupSelectedAsync()
    {
        try
        {
            if (_ungroupPlayer.SelectedItem is not MaPlayer player)
                throw new MaException("Choose a player first.");
            await (await _library.GetClientAsync()).UngroupPlayerAsync(player.PlayerId);
            await RefreshPlayersAsync();
            _status.Text = $"Ungroup command sent for {player.Name}.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task LoadQueueSettingsAsync()
    {
        try
        {
            if (_queuePlayer.SelectedItem is not MaPlayer player) throw new MaException("Choose a player first.");
            var queues = await (await _library.GetClientAsync()).GetQueuesAsync();
            var queue = queues.FirstOrDefault(q => q.QueueId == player.PlayerId);
            if (queue is null)
            {
                _autoplay.IsChecked = false;
                _smartShuffle.IsChecked = false;
                _crossfade.IsChecked = false;
                _crossfadeMode.SelectedIndex = 0;
                _status.Text = $"No active queue state was returned for {player.Name}; settings can still be applied.";
                return;
            }

            _autoplay.IsChecked = queue.Autoplay;
            _smartShuffle.IsChecked = queue.SmartShuffleActive;
            _crossfade.IsChecked = queue.CrossfadeEnabled;
            _status.Text = $"Queue: {queue.ItemCount} item(s), shuffle {(queue.Shuffle ? "on" : "off")}, repeat {queue.Repeat}. Smart Shuffle is only effective while Shuffle is on.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task ApplyQueueSettingsAsync()
    {
        try
        {
            if (_queuePlayer.SelectedItem is not MaPlayer player) throw new MaException("Choose a player first.");
            var client = await _library.GetClientAsync();
            var values = new Dictionary<string, object?>
            {
                ["smart_shuffle_enabled"] = _smartShuffle.IsChecked == true,
                ["crossfade_mode"] = (_crossfadeMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "smart_crossfade"
            };
            await client.SaveQueueSettingsAsync(player.PlayerId, values);
            await client.SetQueueAutoplayAsync(player.PlayerId, _autoplay.IsChecked == true);
            await client.SetQueueCrossfadeAsync(player.PlayerId, _crossfade.IsChecked == true);
            await LoadQueueSettingsAsync();
            _status.Text = $"Queue settings saved for {player.Name}.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task SetSleepTimerAsync(string? mode = null)
    {
        try
        {
            if (_queuePlayer.SelectedItem is not MaPlayer player) throw new MaException("Choose a player first in Intelligent playback.");
            int? seconds = null;
            if (mode is null)
            {
                if (!int.TryParse(_sleepMinutes.Text, out var minutes) || minutes < 1 || minutes > 1440)
                    throw new MaException("Enter a sleep timer between 1 and 1440 minutes.");
                seconds = minutes * 60;
            }
            await (await _library.GetClientAsync()).SetSleepTimerAsync(player.PlayerId, seconds, mode);
            _status.Text = mode is null
                ? $"Sleep timer set for {_sleepMinutes.Text} minute(s) on {player.Name}."
                : $"Sleep timer will stop at {mode.Replace('_', ' ')} on {player.Name}.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task ClearSleepTimerAsync()
    {
        try
        {
            if (_queuePlayer.SelectedItem is not MaPlayer player) throw new MaException("Choose a player first in Intelligent playback.");
            await (await _library.GetClientAsync()).ClearSleepTimerAsync(player.PlayerId);
            _status.Text = $"Sleep timer cleared for {player.Name}.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task LoadMediaAsync()
    {
        try
        {
            var client = await _library.GetClientAsync();
            var kind = (_mediaKind.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            _showingPodcast = null;
            _currentMedia = kind switch
            {
                "audiobooks" => await client.GetLibraryAsync(MaMediaType.Audiobook, limit: 250),
                "podcasts" => await client.GetLibraryAsync(MaMediaType.Podcast, limit: 250),
                "genres" => await client.GetLibraryAsync(MaMediaType.Genre, limit: 250),
                "in_progress" => await client.GetInProgressItemsAsync(100),
                _ => []
            };
            _mediaItems.ItemsSource = _currentMedia;
            _mediaDetails.Text = $"{_currentMedia.Count} item(s). Select an item and choose Play selected.";
            _status.Text = $"Loaded {kind}: {_currentMedia.Count} item(s).";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task LoadPodcastEpisodesAsync()
    {
        try
        {
            if (_mediaItems.SelectedItem is not MaItem { Type: MaMediaType.Podcast } podcast)
                throw new MaException("Select a podcast first.");
            _showingPodcast = podcast;
            _currentMedia = await (await _library.GetClientAsync()).GetPodcastEpisodesAsync(podcast);
            _mediaItems.ItemsSource = _currentMedia;
            _mediaDetails.Text = $"{_currentMedia.Count} episode(s) from {podcast.Name}.";
            _status.Text = $"Loaded episodes for {podcast.Name}.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task PlaySelectedMediaAsync()
    {
        try
        {
            if (_mediaItems.SelectedItem is not MaItem item) throw new MaException("Select an item first.");
            await PlayItemAsync(item);
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task SearchAsync()
    {
        try
        {
            var query = _searchQuery.Text?.Trim() ?? string.Empty;
            if (query.Length == 0) throw new MaException("Enter a search term.");
            var providers = (_searchProviders.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var results = await (await _library.GetClientAsync()).SearchAsync(query, 50, providers: providers.Length == 0 ? null : providers);
            _currentSearch = results.InDisplayOrder.ToList();
            _searchResults.ItemsSource = _currentSearch;
            _status.Text = $"{_currentSearch.Count} search result(s) found.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task PlaySelectedSearchAsync()
    {
        try
        {
            if (_searchResults.SelectedItem is not MaItem item) throw new MaException("Select a search result first.");
            await PlayItemAsync(item);
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task PlayItemAsync(MaItem item)
    {
        if (_queuePlayer.SelectedItem is not MaPlayer player) throw new MaException("Choose a player in Intelligent playback first.");
        if (string.IsNullOrWhiteSpace(item.Uri)) throw new MaException("Music Assistant did not return a playable URI for this item.");
        await (await _library.GetClientAsync()).PlayMediaAsync(player.PlayerId, item.Uri, MaQueueOption.Replace);
        _status.Text = $"Sent {item.Name} to {player.Name}. Resume behaviour is handled by Music Assistant when supported by the media provider.";
    }

    private async Task LoadPlaylistsAsync()
    {
        try
        {
            var client = await _library.GetClientAsync();
            var playlists = await client.GetLibraryAsync(MaMediaType.Playlist, limit: 250, sortField: "name");
            _playlistPicker.ItemsSource = playlists;
            if (_playlistPicker.SelectedIndex < 0 && playlists.Count > 0) _playlistPicker.SelectedIndex = 0;
            _status.Text = $"Loaded {playlists.Count} playlist(s).";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task CreatePlaylistAsync()
    {
        try
        {
            var created = await (await _library.GetClientAsync()).CreatePlaylistAsync(_playlistName.Text ?? string.Empty);
            await LoadPlaylistsAsync();
            if (created is not null) _status.Text = $"Created playlist: {created.Name}.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task LoadPlaylistTracksAsync()
    {
        try
        {
            if (_playlistPicker.SelectedItem is not MaItem playlist) throw new MaException("Choose a playlist first.");
            _currentPlaylistTracks = await (await _library.GetClientAsync()).GetChildrenAsync(playlist);
            _playlistTracks.ItemsSource = _currentPlaylistTracks;
            _status.Text = $"{_currentPlaylistTracks.Count} track(s) in {playlist.Name}.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task AddSearchResultToPlaylistAsync()
    {
        try
        {
            if (_playlistPicker.SelectedItem is not MaItem playlist) throw new MaException("Choose a destination playlist first.");
            if (_searchResults.SelectedItem is not MaItem { Type: MaMediaType.Track } track)
                throw new MaException("Select a track in Provider-aware search first.");
            await (await _library.GetClientAsync()).AddToPlaylistAsync(playlist.ItemId, [track.Uri]);
            await LoadPlaylistTracksAsync();
            _status.Text = $"Added {track.Name} to {playlist.Name}.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task RemovePlaylistTracksAsync()
    {
        try
        {
            if (_playlistPicker.SelectedItem is not MaItem playlist) throw new MaException("Choose a playlist first.");
            var selected = _playlistTracks.SelectedItems.Cast<MaItem>().ToHashSet();
            if (selected.Count == 0) throw new MaException("Select one or more playlist tracks to remove.");
            var positions = Enumerable.Range(0, _currentPlaylistTracks.Count)
                .Where(i => selected.Contains(_currentPlaylistTracks[i])).ToList();
            await (await _library.GetClientAsync()).RemovePlaylistTracksAsync(playlist, positions);
            await LoadPlaylistTracksAsync();
            _status.Text = $"Removed {positions.Count} track(s) from {playlist.Name}.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task ExportPlaylistAsync()
    {
        try
        {
            if (_playlistPicker.SelectedItem is not MaItem playlist) throw new MaException("Choose a playlist first.");
            _m3uData.Text = await (await _library.GetClientAsync()).ExportPlaylistAsync(playlist);
            _status.Text = $"Exported {playlist.Name} as M3U8. The content is in the text area below.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private async Task ImportPlaylistAsync()
    {
        try
        {
            var imported = await (await _library.GetClientAsync()).ImportPlaylistAsync(_m3uData.Text ?? string.Empty);
            await LoadPlaylistsAsync();
            _status.Text = imported is null ? "Playlist import was submitted." : $"Imported playlist: {imported.Name}.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private void CopyPlaylistExport()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_m3uData.Text)) throw new MaException("Export a playlist first.");
            var package = new DataPackage();
            package.SetText(_m3uData.Text);
            Clipboard.SetContent(package);
            _status.Text = "M3U8 content copied to the clipboard.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private void OpenMusicAssistant()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_serverUrl))
                throw new MaException("Connect to Music Assistant first.");
            Process.Start(new ProcessStartInfo(_serverUrl) { UseShellExecute = true });
            _status.Text = "Opened the Music Assistant web interface. Configure optional plugins under Settings → Plugins.";
        }
        catch (Exception ex) { SetStatus(ex); }
    }

    private void SetStatus(Exception ex) =>
        _status.Text = ex is MaException ? ex.Message : $"Music Assistant operation failed: {ex.Message}";
}
