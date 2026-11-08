using MaSatellite.Host.NowPlaying;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MaSatellite.App.Views;

/// <summary>The Lyrics, Related and About tabs. Each loads only when it is first opened for a track and never touches playback.</summary>
public sealed partial class NowPlayingPage
{
    private static readonly TimeSpan ContextWait = TimeSpan.FromSeconds(5);

    private string? _lyricsFor;
    private string? _relatedFor;
    private string? _aboutFor;
    private IReadOnlyList<LyricLine> _lines = [];
    private readonly List<TextBlock> _lineBlocks = [];
    private int _activeLine = -1;
    private double _positionSeconds;

    /// <summary>The first credited artist, which is what lyric and metadata services index by.</summary>
    private static string PrimaryArtist(string artists)
    {
        var cut = artists.IndexOfAny([',', '/', '&', ';']);
        return (cut > 0 ? artists[..cut] : artists).Trim();
    }

    /// <summary>Waits briefly for the queue lookup that supplies the Music Assistant id; continues without it when slow.</summary>
    private async Task WaitForContextAsync(CancellationToken ct)
    {
        try
        {
            await _context.WaitAsync(ContextWait, ct);
        }
        catch (TimeoutException)
        {
            // The tab works from title and artist alone.
        }
    }

    private static void ShowPanelStatus(Panel panel, ProgressRing ring, TextBlock text, Button retry, string? message, bool busy, bool canRetry)
    {
        panel.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        text.Text = message ?? string.Empty;
        ring.IsActive = busy;
        ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        retry.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Lyrics ----

    private void ShowLyricsStatus(string? message, bool busy, bool retry) =>
        ShowPanelStatus(LyricsStatus, LyricsBusy, LyricsStatusText, LyricsRetry, message, busy, retry);

    private void ClearLyrics()
    {
        _lines = [];
        _lineBlocks.Clear();
        _activeLine = -1;
        LyricsHost.Children.Clear();
        LyricsSource.Text = string.Empty;
        ShowLyricsStatus(null, false, false);
    }

    private async Task LoadLyricsAsync()
    {
        if (_controller is null)
        {
            return;
        }

        if (_trackKey.Length == 0)
        {
            ShowLyricsStatus("Nothing is playing.", false, false);
            return;
        }

        if (_lyricsFor == _trackKey)
        {
            return;
        }

        var key = _trackKey;
        var ct = _trackCts.Token;
        _lyricsFor = key;
        ClearLyrics();
        ShowLyricsStatus("Looking for lyrics…", busy: true, retry: false);
        try
        {
            await WaitForContextAsync(ct);
            var result = await _controller.GetLyricsAsync(new LyricsQuery(_title, PrimaryArtist(_artist), _album, _durationSeconds), _trackUri, ct);
            if (ct.IsCancellationRequested || key != _trackKey)
            {
                return;
            }

            ApplyLyrics(result);
        }
        catch (OperationCanceledException)
        {
            // The track changed; the next one loads its own lyrics.
        }
    }

    private void ApplyLyrics(LyricsResult result)
    {
        ClearLyrics();
        switch (result.Kind)
        {
            case LyricsKind.Synced:
                LyricsSource.Text = $"Synced · via {result.Source}";
                _lines = result.Lines;
                foreach (var line in result.Lines)
                {
                    var block = new TextBlock
                    {
                        Text = line.Text.Length == 0 ? "♪" : line.Text,
                        FontSize = 24,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = R("AuroraText"),
                        Opacity = 0.38,
                        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) },
                    };
                    var start = line.StartSeconds;
                    block.Tapped += (_, _) => SeekRequested?.Invoke(this, (long)(start * 1000));
                    _lineBlocks.Add(block);
                    LyricsHost.Children.Add(block);
                }

                FollowLyrics(_positionSeconds, forceScroll: false);
                break;

            case LyricsKind.Plain:
                LyricsSource.Text = $"Not time-synced · via {result.Source}";
                LyricsHost.Children.Add(new TextBlock
                {
                    Text = result.PlainText,
                    FontSize = 17,
                    LineHeight = 28,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = R("AuroraText"),
                    Opacity = 0.92,
                    IsTextSelectionEnabled = true,
                });
                break;

            case LyricsKind.Instrumental:
                ShowLyricsStatus("This track is instrumental, so there are no lyrics.", false, false);
                break;

            case LyricsKind.Error:
                _lyricsFor = null;
                ShowLyricsStatus(result.Message ?? "Lyrics could not be loaded right now.", false, retry: true);
                break;

            default:
                ShowLyricsStatus("No lyrics were found for this track.", false, false);
                break;
        }
    }

    /// <summary>Highlights the line being sung and, unless the reader is scrolling, keeps it in view.</summary>
    private void FollowLyrics(double positionSeconds, bool forceScroll = false)
    {
        _positionSeconds = positionSeconds;
        if (_lines.Count == 0)
        {
            return;
        }

        var index = LrcParser.IndexAt(_lines, positionSeconds);
        if (index == _activeLine && !forceScroll)
        {
            return;
        }

        if (_activeLine >= 0 && _activeLine < _lineBlocks.Count)
        {
            _lineBlocks[_activeLine].Opacity = 0.38;
        }

        _activeLine = index;
        if (index < 0 || index >= _lineBlocks.Count)
        {
            return;
        }

        _lineBlocks[index].Opacity = 1;
        var userIsReading = DateTimeOffset.UtcNow - _userScrolledAt < UserScrollPause;
        if (_tab == "lyrics" && _shown && !userIsReading)
        {
            _lineBlocks[index].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.3 });
        }
    }

    // ---- Related ----

    private void ShowRelatedStatus(string? message, bool busy, bool retry) =>
        ShowPanelStatus(RelatedStatusPanel, RelatedBusy, RelatedStatusText, RelatedRetry, message, busy, retry);

    private async Task LoadRelatedAsync()
    {
        if (_controller is null)
        {
            return;
        }

        if (_trackKey.Length == 0)
        {
            ShowRelatedStatus("Nothing is playing.", false, false);
            return;
        }

        if (_relatedFor == _trackKey)
        {
            return;
        }

        var key = _trackKey;
        var ct = _trackCts.Token;
        _relatedFor = key;
        RelatedHost.Children.Clear();
        ShowRelatedStatus("Finding related music…", busy: true, retry: false);
        try
        {
            await WaitForContextAsync(ct);
            if (string.IsNullOrEmpty(_trackUri))
            {
                ShowRelatedStatus("Related music comes from Music Assistant and needs the playing track to be in its queue.", false, false);
                return;
            }

            var related = await _controller.GetRelatedAsync(_trackUri, ct);
            if (ct.IsCancellationRequested || key != _trackKey)
            {
                return;
            }

            ShowRelatedStatus(null, false, false);
            foreach (var section in related.Sections)
            {
                RelatedHost.Children.Add(BuildRelatedSection(section));
            }

            if (related.Sections.All(s => s.Status != RelatedStatus.Ok))
            {
                ShowRelatedStatus("Your Music Assistant providers have no recommendations for this track. Nothing is made up to fill the gap.", false, false);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by the next track.
        }
        catch (MaException ex)
        {
            _relatedFor = null;
            ShowRelatedStatus("Related music could not be loaded. " + ex.Message, false, retry: true);
        }
    }

    private UIElement BuildRelatedSection(RelatedSection section)
    {
        if (section.Status != RelatedStatus.Ok)
        {
            return new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    SectionTitle(section.Title),
                    new TextBlock { Text = section.Message ?? "Nothing to show.", FontSize = 13, Foreground = R("AuroraMuted"), TextWrapping = TextWrapping.Wrap },
                },
            };
        }

        var caption = $"From {section.Source}";
        if (section.Items.All(i => i.Type == MaMediaType.Track))
        {
            var list = new StackPanel { Spacing = 2 };
            foreach (var item in section.Items)
            {
                var row = new MediaRow { Row = ListRow.From(item) };
                row.PlayRequested += (s, a) => RowPlayRequested?.Invoke(s, a);
                row.MenuRequested += (s, a) => RowMenuRequested?.Invoke(s, a);
                list.Children.Add(row);
            }

            return new StackPanel { Spacing = 6, Children = { SectionTitle(section.Title), CaptionText(caption), list } };
        }

        var shelf = new Shelf
        {
            Title = section.Title,
            Subtitle = caption,
            ItemTemplate = CardTemplate,
            ItemsSource = section.Items.Select(i => ListRow.From(i)).ToList(),
        };
        shelf.ItemClick += (s, a) => ItemClick?.Invoke(s, a);
        return shelf;
    }

    // ---- About ----

    private void ShowAboutStatus(string? message, bool busy, bool retry) =>
        ShowPanelStatus(AboutStatus, AboutBusy, AboutStatusText, AboutRetry, message, busy, retry);

    private async Task LoadAboutAsync()
    {
        if (_controller is null)
        {
            return;
        }

        if (_trackKey.Length == 0)
        {
            ShowAboutStatus("Nothing is playing.", false, false);
            return;
        }

        if (_aboutFor == _trackKey)
        {
            return;
        }

        var key = _trackKey;
        var ct = _trackCts.Token;
        _aboutFor = key;
        AboutHost.Children.Clear();
        ShowAboutStatus("Gathering information about this track…", busy: true, retry: false);
        try
        {
            await WaitForContextAsync(ct);
            var info = await _controller.GetAboutAsync(_trackUri, _title, PrimaryArtist(_artist), _durationSeconds, ct);
            if (ct.IsCancellationRequested || key != _trackKey)
            {
                return;
            }

            ShowAboutStatus(null, false, false);
            BuildAbout(info);
        }
        catch (OperationCanceledException)
        {
            // Superseded by the next track.
        }
        catch (Exception ex) when (ex is HttpRequestException or MaException)
        {
            _aboutFor = null;
            ShowAboutStatus("Information about this track could not be loaded. " + ex.Message, false, retry: true);
        }
    }

    private void BuildAbout(AboutInfo info)
    {
        AboutHost.Children.Clear();
        if (info.IsEmpty)
        {
            ShowAboutStatus("No further information is available for this track yet.", false, false);
        }

        // How to read the labels, and how the track was identified.
        var legend = new StackPanel { Spacing = 8 };
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        badges.Children.Add(TrustBadge(InfoTrust.Verified, "Verified metadata"));
        badges.Children.Add(TrustBadge(InfoTrust.Library, "Your library"));
        badges.Children.Add(TrustBadge(InfoTrust.Community, "Community text"));
        legend.Children.Add(badges);
        if (info.MatchNote is not null)
        {
            legend.Children.Add(CaptionText(info.MatchNote));
        }

        foreach (var warning in info.Warnings)
        {
            legend.Children.Add(new TextBlock { Text = warning, FontSize = 12, Foreground = R("AuroraMuted"), TextWrapping = TextWrapping.Wrap });
        }

        AboutHost.Children.Add(legend);

        if (info.TrackFacts.Count > 0 || info.ReleaseArtworkUrl is not null)
        {
            var body = new StackPanel { Spacing = 14 };
            body.Children.Add(SectionTitle("This track"));
            body.Children.Add(TileGrid(info.TrackFacts.Select(FactTile)));
            if (info.Tags.Count > 0)
            {
                body.Children.Add(Chips(info.Tags));
            }

            UIElement content = body;
            if (info.ReleaseArtworkUrl is { } art && Uri.TryCreate(art, UriKind.Absolute, out var artUri))
            {
                // Shown only when the Cover Art Archive actually has a cover for this release.
                var image = new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage(artUri) { DecodePixelWidth = 320 } };
                var frame = new Border { Width = 140, Height = 140, CornerRadius = new CornerRadius(14), Child = image };
                var caption = CaptionText("Release artwork · Cover Art Archive");
                var side = new StackPanel { Spacing = 6, Width = 140, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top, Children = { frame, caption } };
                image.ImageOpened += (_, _) => side.Visibility = Visibility.Visible;
                var layout = new Grid { ColumnSpacing = 20 };
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                layout.Children.Add(body);
                Grid.SetColumn(side, 1);
                layout.Children.Add(side);
                content = layout;
            }

            var card = Card(content);
            AboutHost.Children.Add(card);
        }

        foreach (var artist in info.Artists)
        {
            AboutHost.Children.Add(BuildArtistCard(artist));
        }
    }

    private UIElement BuildArtistCard(ArtistAbout artist)
    {
        var body = new StackPanel { Spacing = 12 };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(CaptionText("ARTIST"));
        title.Children.Add(new TextBlock { Text = artist.Name, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = R("AuroraText"), TextWrapping = TextWrapping.Wrap });
        header.Children.Add(title);
        if (!string.IsNullOrEmpty(artist.MusicAssistantUri))
        {
            var open = new Button { Content = "Open artist", Style = (Style)Application.Current.Resources["AuroraButtonStyle"], Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(16) };
            var uri = artist.MusicAssistantUri;
            open.Click += async (_, _) => await OpenItemByUriAsync(uri);
            Grid.SetColumn(open, 1);
            header.Children.Add(open);
        }

        body.Children.Add(header);
        if (artist.Facts.Count > 0)
        {
            body.Children.Add(TileGrid(artist.Facts.Select(FactTile)));
        }

        if (artist.Genres.Count > 0)
        {
            body.Children.Add(Chips(artist.Genres));
        }

        if (artist.Biography is not null)
        {
            body.Children.Add(new TextBlock { Text = artist.Biography, FontSize = 14, LineHeight = 22, TextWrapping = TextWrapping.Wrap, Foreground = R("AuroraText"), Opacity = 0.9 });
            var source = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            source.Children.Add(TrustBadge(InfoTrust.Community, "Community text"));
            source.Children.Add(CaptionText(artist.BiographySource ?? string.Empty));
            body.Children.Add(source);
        }
        else
        {
            body.Children.Add(CaptionText("No biography is available from your Music Assistant providers."));
        }

        var links = artist.Links.Where(l => Uri.TryCreate(l.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https").ToList();
        if (links.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var link in links)
            {
                row.Children.Add(new HyperlinkButton { Content = LinkLabel(link.Type), NavigateUri = new Uri(link.Url), Padding = new Thickness(8, 2, 8, 2) });
            }

            body.Children.Add(row);
        }

        return Card(body);
    }

    private static string LinkLabel(string type) => type switch
    {
        "official homepage" or "website" => "Website",
        "wikipedia" => "Wikipedia",
        "wikidata" => "Wikidata",
        "discogs" => "Discogs",
        "bandcamp" => "Bandcamp",
        _ => char.ToUpperInvariant(type[0]) + type[1..],
    };

    // ---- Small building blocks ----

    private static Border Card(UIElement child) => new()
    {
        CornerRadius = new CornerRadius(20),
        Padding = new Thickness(22, 18, 22, 20),
        Background = R("AuroraCardSoft"),
        BorderBrush = R("AuroraBorder"),
        BorderThickness = new Thickness(1),
        Child = child,
    };

    private static TextBlock SectionTitle(string text) => new() { Text = text, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = R("AuroraText") };

    private static TextBlock CaptionText(string text) => new() { Text = text, FontSize = 12, Foreground = R("AuroraMuted"), TextWrapping = TextWrapping.Wrap };

    private static Border TrustBadge(InfoTrust trust, string text)
    {
        var colour = trust switch
        {
            InfoTrust.Verified => R("AuroraGood"),
            InfoTrust.Library => R("AuroraAccent"),
            _ => new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xC1, 0x4E)),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = colour, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = R("AuroraMuted") });
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 10, 3),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            Child = content,
        };
    }

    private static Border FactTile(AboutFact fact)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = fact.Label.ToUpperInvariant(), FontSize = 10, CharacterSpacing = 60, Foreground = R("AuroraMuted") });
        stack.Children.Add(new TextBlock { Text = fact.Value, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = R("AuroraText"), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(TrustBadge(fact.Trust, fact.Trust switch
        {
            InfoTrust.Verified => $"Verified · {fact.Source}",
            InfoTrust.Library => "Your library",
            _ => $"Community · {fact.Source}",
        }));
        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12),
            Background = R("AuroraCard"),
            Child = stack,
        };
    }

    /// <summary>Tags that wrap onto further lines as the window narrows.</summary>
    private static RichTextBlock Chips(IEnumerable<string> tags)
    {
        var block = new RichTextBlock();
        var paragraph = new Paragraph();
        foreach (var tag in tags)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 4, 12, 5),
                Margin = new Thickness(0, 0, 8, 8),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x26, 0x9B, 0x8C, 0xFF)),
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x66, 0x9B, 0x8C, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = tag, FontSize = 12, Foreground = R("AuroraText") },
            };
            paragraph.Inlines.Add(new InlineUIContainer { Child = chip });
        }

        block.Blocks.Add(paragraph);
        return block;
    }

    /// <summary>Equal-width tiles whose column count follows the available width.</summary>
    private static Grid TileGrid(IEnumerable<FrameworkElement> tiles)
    {
        const double MinTileWidth = 220;
        var items = tiles.ToList();
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        var columns = 0;

        void Arrange(double width)
        {
            var wanted = Math.Max(1, Math.Min(items.Count, (int)((width + 12) / (MinTileWidth + 12))));
            if (wanted == columns)
            {
                return;
            }

            columns = wanted;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (var c = 0; c < columns; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (var r = 0; r < (items.Count + columns - 1) / columns; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (var i = 0; i < items.Count; i++)
            {
                Grid.SetColumn(items[i], i % columns);
                Grid.SetRow(items[i], i / columns);
            }
        }

        foreach (var item in items)
        {
            grid.Children.Add(item);
        }

        Arrange(MinTileWidth);
        grid.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        return grid;
    }
}
