using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MaSatellite.App.Views;

/// <summary>A square-artwork card for albums, artists, tracks and playlists (standard and compact sizes).</summary>
public sealed partial class MediaCard : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(ListRow), typeof(MediaCard), new PropertyMetadata(null, (d, _) => ((MediaCard)d).Apply()));

    public static readonly DependencyProperty ArtSizeProperty = DependencyProperty.Register(
        nameof(ArtSize), typeof(double), typeof(MediaCard), new PropertyMetadata(164d, (d, _) => ((MediaCard)d).ApplySize()));

    private static readonly Brush TransparentBrush = new SolidColorBrush(Colors.Transparent);
    private readonly ScaleTransform _zoom = new() { CenterX = 0.5, CenterY = 0.5, ScaleX = 1, ScaleY = 1 };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomTimer;
    private bool _hover;
    private bool _focus;

    public MediaCard()
    {
        InitializeComponent();
        PlayButton.Translation = new Vector3(0, 8, 0);
        Unloaded += (_, _) => _zoomTimer?.Stop();
    }

    public event EventHandler<RowAction>? PlayRequested;

    public event EventHandler<RowAction>? MenuRequested;

    public ListRow? Row
    {
        get => (ListRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public double ArtSize
    {
        get => (double)GetValue(ArtSizeProperty);
        set => SetValue(ArtSizeProperty, value);
    }

    private void ApplySize()
    {
        ArtHost.Width = ArtHost.Height = ArtSize;
        TextHost.Width = ArtSize;
    }

    private void Apply()
    {
        var row = Row;
        if (row is null)
        {
            ArtRect.Fill = null;
            return;
        }

        TitleText.Text = row.Title;
        SubtitleText.Text = row.CardSubtitle;
        ToolTipService.SetToolTip(TitleText, row.Title);
        AutomationProperties.SetName(this, string.IsNullOrEmpty(row.CardSubtitle) ? row.Title : $"{row.Title}, {row.CardSubtitle}");
        Placeholder.Visibility = row.Image is null ? Visibility.Visible : Visibility.Collapsed;
        ArtRect.Fill = row.Image is null ? null : new ImageBrush
        {
            ImageSource = row.Image,
            Stretch = Stretch.UniformToFill,
            RelativeTransform = _zoom,
        };
        var playable = row.Item is { IsPlayable: true };
        PlayButton.Visibility = MenuButton.Visibility = playable ? Visibility.Visible : Visibility.Collapsed;
        _zoom.ScaleX = _zoom.ScaleY = 1;
        UpdateState(animate: false);
    }

    private void UpdateState(bool animate = true)
    {
        var active = _hover || _focus;
        Root.Background = active ? (Brush)Application.Current.Resources["AuroraHover"] : TransparentBrush;
        PlayButton.Opacity = MenuButton.Opacity = active ? 1 : 0;
        PlayButton.Translation = new Vector3(0, active ? 0 : 8, 0);

        var target = active ? 1.05 : 1.0;
        _zoomTimer?.Stop();
        if (!animate)
        {
            _zoom.ScaleX = _zoom.ScaleY = target;
            return;
        }

        _zoomTimer = Motion.Tween(DispatcherQueue, _zoom.ScaleX, target, 180, v => _zoom.ScaleX = _zoom.ScaleY = v);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) { _hover = true; UpdateState(); }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) { _hover = false; UpdateState(); }

    private void OnGotFocus(object sender, RoutedEventArgs e) { _focus = true; UpdateState(); }

    private void OnLostFocus(object sender, RoutedEventArgs e) { _focus = false; UpdateState(); }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Row is { } row) PlayRequested?.Invoke(this, new RowAction(row, PlayButton));
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (Row is { } row) MenuRequested?.Invoke(this, new RowAction(row, MenuButton));
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Row is { Item.IsPlayable: true } row)
        {
            MenuRequested?.Invoke(this, new RowAction(row, Root));
            e.Handled = true;
        }
    }
}
