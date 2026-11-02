using MaSatellite.MaApi.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using FontWeight = global::Windows.UI.Text.FontWeight;

namespace MaSatellite.App.Views;

/// <summary>One line in the library/queue list: a media item or a queue entry.</summary>
public sealed class ListRow
{
    public ListRow(string title, string subtitle, string? imageUrl, MaItem? item = null, MaQueueItem? queueItem = null, bool isCurrent = false, int? position = null)
    {
        Title = title;
        Subtitle = subtitle;
        Item = item;
        QueueItem = queueItem;
        IsCurrent = isCurrent;
        Position = position;
        if (!string.IsNullOrEmpty(imageUrl) && Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            Image = new BitmapImage(uri) { DecodePixelWidth = 256 };
        }
    }

    public string Title { get; }

    public string Subtitle { get; }

    public ImageSource? Image { get; }

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
