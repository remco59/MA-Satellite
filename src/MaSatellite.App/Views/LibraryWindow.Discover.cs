using MaSatellite.Host.Presentation;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace MaSatellite.App.Views;

/// <summary>A tile on the Discover page: a title, one line of detail and what it scrolls to.</summary>
public sealed record ExploreTile(string Key, string Glyph, string Title, string Detail);

// Discover: built only from what Music Assistant returns (history, play counts, library tags).
// Nothing here is predicted or generated, and every section disappears when its data is missing.
public sealed partial class LibraryWindow
{
    private IEnumerable<Shelf> DiscoverShelfControls =>
    [
        DiscoverBecauseShelf, DiscoverSimilarShelf, DiscoverNewShelf, DiscoverGenreShelf, DiscoverUndiscoveredShelf,
    ];

    private async Task<DiscoverLayout> LoadDiscoverAsync()
    {
        var client = await _controller.Service.GetClientAsync();
        IReadOnlyList<MaItem> recent = [];
        try
        {
            recent = await client.GetRecentlyPlayedAsync(60, ["track", "album"]);
        }
        catch (MaException)
        {
            // No listening history on this server: sections that need it are hidden, never faked.
        }

        var albumsTask = TryLibraryAsync(client, MaMediaType.Album, null, null, 300);
        var artistsTask = TryLibraryAsync(client, MaMediaType.Artist, "play_count", "desc", 40);
        await Task.WhenAll(albumsTask, artistsTask);
        var albums = albumsTask.Result;
        var artists = LibrarySorter.Sort(artistsTask.Result, "popular");
        _discoverAlbums = albums;

        // Similar artists seed from the artist you play most, if the server has play counts for artists.
        IReadOnlyList<MaItem> similar = [];
        string? seedName = null;
        var seed = artists.FirstOrDefault(a => (a.PlayCount ?? 0) > 0);
        if (seed is not null)
        {
            try
            {
                similar = await client.GetSimilarArtistsAsync(seed, 16);
                seedName = seed.Name;
            }
            catch (MaException)
            {
                // Similar artists depend on the metadata provider.
            }
        }

        var because = DiscoverComposer.Because(recent, albums);
        return DiscoverComposer.Compose(recent, albums, artists, similar, seedName,
            because?.Items ?? [], because is { } b ? b.Title : null);
    }

    /// <summary>Fills the page. Returns true when nothing at all could be shown.</summary>
    private bool RenderDiscover(DiscoverLayout layout)
    {
        static List<ListRow> Rows(IEnumerable<MaItem> items) => items.Select(i => ListRow.From(i)).ToList();

        // Insight banner
        InsightPanel.Visibility = layout.Insight is null ? Visibility.Collapsed : Visibility.Visible;
        InsightArtists.Children.Clear();
        if (layout.Insight is { } insight)
        {
            InsightTitle.Text = insight.Headline;
            InsightDetail.Text = insight.Detail;
            foreach (var artist in insight.Artists)
            {
                InsightArtists.Children.Add(CreateArtistBubble(artist));
            }
        }

        SetShelf(DiscoverBecauseShelf, Rows(layout.BecauseYouPlayed), layout.BecauseTitle ?? string.Empty,
            "More from the same artist in your library.");
        SetShelf(DiscoverSimilarShelf, Rows(layout.SimilarArtists),
            layout.SimilarSeed is null ? "Similar artists" : $"Similar to {layout.SimilarSeed}",
            "Suggested by your metadata provider, based on the artist you play most.");
        SetShelf(DiscoverNewShelf, Rows(layout.NewAlbums));
        SetShelf(DiscoverUndiscoveredShelf, Rows(layout.Undiscovered));

        GenreChips.ItemsSource = layout.Genres.Count > 0 ? layout.Genres : null;
        GenreSection.Visibility = layout.Genres.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetShelf(DiscoverGenreShelf, []);

        // Only statistics the server actually reported: a zero from a server without play counts would be misleading.
        var statTiles = new List<ExploreTile>();
        if (layout.Stats is { } stats)
        {
            if (stats.TotalPlays > 0) statTiles.Add(new("plays", "\uE768", stats.TotalPlays.ToString("N0"), "album plays counted in your library"));
            if (stats.PlayedAlbums > 0) statTiles.Add(new("albums", "\uE93C", stats.PlayedAlbums.ToString("N0"), "albums you have played"));
            if (stats.DistinctArtists > 0) statTiles.Add(new("artists", "\uE77B", stats.DistinctArtists.ToString("N0"), "artists in your recent listening"));
            if (stats.TopArtist is not null) statTiles.Add(new("top", "\uE734", stats.TopArtist, "your most played artist"));
        }

        StatsList.ItemsSource = statTiles.Count > 0 ? statTiles : null;
        StatsPanel.Visibility = statTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Quick links to the sections that exist.
        var tiles = new List<ExploreTile>();
        if (layout.SimilarArtists.Count > 0)
        {
            tiles.Add(new("similar", "\uE77B", "Similar artists", layout.SimilarSeed is null ? "Based on your favourites" : $"Based on {layout.SimilarSeed}"));
        }

        if (layout.BecauseYouPlayed.Count > 0)
        {
            tiles.Add(new("because", "\uE734", "Recommended for you", "More from artists you play"));
        }

        if (layout.NewAlbums.Count > 0)
        {
            tiles.Add(new("new", "\uE787", "New releases", "Recently added to your library"));
        }

        if (layout.Genres.Count > 0)
        {
            tiles.Add(new("genres", "\uE8EC", "Explore genres", $"{layout.Genres.Count} genres in your library"));
        }

        if (statTiles.Count > 0)
        {
            tiles.Add(new("stats", "\uE9D9", "Listening stats", "Your music in numbers"));
        }

        // A lone shortcut is not worth a section of its own.
        var showTiles = tiles.Count >= 2;
        ExploreTiles.ItemsSource = showTiles ? tiles : null;
        ExploreSection.Visibility = showTiles ? Visibility.Visible : Visibility.Collapsed;

        return layout.Insight is null && tiles.Count == 0 && layout.Undiscovered.Count == 0 && layout.NewAlbums.Count == 0;
    }

    private void ClearDiscover()
    {
        DiscoverView.Visibility = Visibility.Collapsed;
        InsightPanel.Visibility = ExploreSection.Visibility = GenreSection.Visibility = StatsPanel.Visibility = Visibility.Collapsed;
        foreach (var shelf in DiscoverShelfControls)
        {
            SetShelf(shelf, []);
        }
    }

    /// <summary>A round artist picture that opens the artist.</summary>
    private Button CreateArtistBubble(MaItem artist)
    {
        var row = ListRow.From(artist);
        var face = new Ellipse
        {
            Width = 52,
            Height = 52,
            Fill = row.Image is null
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 42, 37, 86))
                : new ImageBrush { ImageSource = row.Image, Stretch = Stretch.UniformToFill },
            Stroke = (Brush)Application.Current.Resources["AuroraBackground"],
            StrokeThickness = 3,
        };
        var button = new Button
        {
            Content = face,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Style = (Style)Application.Current.Resources["AuroraSurfaceButtonStyle"],
        };
        ToolTipService.SetToolTip(button, artist.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Open {artist.Name}");
        button.Click += async (_, _) => await GoToAsync(new NavState(artist.Type == MaMediaType.Artist ? "artist" : "album", artist));
        return button;
    }

    private void OnExploreTile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExploreTile tile)
        {
            return;
        }

        FrameworkElement? target = tile.Key switch
        {
            "similar" => DiscoverSimilarShelf,
            "because" => DiscoverBecauseShelf,
            "new" => DiscoverNewShelf,
            "genres" => GenreSection,
            "stats" => StatsPanel,
            _ => null,
        };
        target?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = Motion.Enabled, VerticalAlignmentRatio = 0.1 });
    }

    /// <summary>Shows the library albums tagged with the chosen genre.</summary>
    private void OnGenreClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GenreCount genre)
        {
            return;
        }

        var albums = _discoverAlbums
            .Where(a => a.IsPlayable && (a.Genres?.Any(g => g.Equals(genre.Genre, StringComparison.CurrentCultureIgnoreCase)) ?? false))
            .Select(a => ListRow.From(a)).ToList();
        SetShelf(DiscoverGenreShelf, albums, $"{genre.Genre} albums", $"{albums.Count} in your library tagged {genre.Genre}.");
        DiscoverGenreShelf.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = Motion.Enabled });
    }
}
