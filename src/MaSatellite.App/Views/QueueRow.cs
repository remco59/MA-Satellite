using MaSatellite.Host.Presentation;
using MaSatellite.MaApi.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MaSatellite.App.Views;

/// <summary>One entry of the Up Next list. <see cref="UpcomingIndex"/> is its place among the upcoming tracks, used to turn a drag into a queue move.</summary>
public sealed class QueueRow
{
    public QueueRow(MaQueueItem item)
    {
        Item = item;
        Title = item.Name;
        Artist = item.Artist ?? item.Subtitle ?? string.Empty;
        Album = item.Album ?? string.Empty;
        if (!string.IsNullOrEmpty(item.ImageUrl) && Uri.TryCreate(item.ImageUrl, UriKind.Absolute, out var uri))
        {
            Image = new BitmapImage(uri) { DecodePixelWidth = 96 };
        }
    }

    public MaQueueItem Item { get; }

    public string Title { get; }

    public string Artist { get; }

    public string Album { get; }

    public ImageSource? Image { get; }

    public int UpcomingIndex { get; set; }

    public string DurationLabel => Item.DurationSeconds is > 0 and var s ? ProgressFormatter.Time(s * 1000L) : string.Empty;

    public Visibility PlaceholderVisibility => Image is null ? Visibility.Visible : Visibility.Collapsed;

    public string ArtistAlbum => string.IsNullOrEmpty(Album) ? Artist : $"{Artist} · {Album}";
}
