using MaSatellite.MaApi.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using FontWeight = global::Windows.UI.Text.FontWeight;

namespace MaSatellite.App.Views;

/// <summary>Raised by cards and rows: the row that was acted on and the element a flyout should anchor to.</summary>
public sealed record RowAction(ListRow Row, FrameworkElement Anchor);

/// <summary>One line in the library/queue list: a media item or a queue entry.</summary>
public sealed class ListRow
{
    private readonly string? _imageUrl;
    private ImageSource? _largeImage;

    public ListRow(string title, string subtitle, string? imageUrl, MaItem? item = null, MaQueueItem? queueItem = null, bool isCurrent = false, int? position = null)
    {
        Title = title;
        Subtitle = subtitle;
        Item = item;
        QueueItem = queueItem;
        IsCurrent = isCurrent;
        Position = position;
        _imageUrl = imageUrl;
        if (!string.IsNullOrEmpty(imageUrl) && Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            Image = new BitmapImage(uri) { DecodePixelWidth = 256 };
        }
    }

    public string Title { get; }

    public string Subtitle { get; }

    public ImageSource? Image { get; }

    /// <summary>A higher-resolution decode of the artwork for featured cards.</summary>
    public ImageSource? LargeImage => _largeImage ??= !string.IsNullOrEmpty(_imageUrl) &&
        Uri.TryCreate(_imageUrl, UriKind.Absolute, out var uri) ? new BitmapImage(uri) { DecodePixelWidth = 720 } : Image;

    /// <summary>Second line on cards: what the item is, plus who it belongs to when that is known.</summary>
    public string CardSubtitle => Item?.Type switch
    {
        MaMediaType.Playlist => string.IsNullOrWhiteSpace(Item.Subtitle) ? "Playlist" : $"Playlist · {Item.Subtitle}",
        MaMediaType.Artist => "Artist",
        MaMediaType.Radio => "Radio",
        MaMediaType.Album or MaMediaType.Track when !string.IsNullOrWhiteSpace(Item.Subtitle) => Item.Subtitle,
        _ => Subtitle,
    };

    public MaItem? Item { get; }

    public MaQueueItem? QueueItem { get; }

    public bool IsCurrent { get; }

    public int? Position { get; }

    public string PositionLabel => Position?.ToString() ?? string.Empty;

    public Visibility PositionVisibility => Position.HasValue ? Visibility.Visible : Visibility.Collapsed;

    public string DurationLabel
    {
        get
        {
            var seconds = Item?.DurationSeconds ?? QueueItem?.DurationSeconds;
            if (seconds is not > 0) return string.Empty;
            var time = TimeSpan.FromSeconds(seconds.Value);
            return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
        }
    }

    public Visibility DurationVisibility => DurationLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public FontWeight Weight => IsCurrent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public Visibility PlaceholderVisibility => Image is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MenuVisibility => Item is { IsPlayable: true } ? Visibility.Visible : Visibility.Collapsed;

    public static ListRow From(MaItem item, bool showType = false, int? position = null)
    {
        var subtitle = item.Description;
        if (showType)
        {
            subtitle = string.IsNullOrEmpty(subtitle) ? item.Type.ToString() : $"{item.Type} · {subtitle}";
        }

        return new ListRow(item.Name, subtitle, item.ImageUrl, item, position: position);
    }
}
