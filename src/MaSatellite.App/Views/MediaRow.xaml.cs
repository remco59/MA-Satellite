using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MaSatellite.App.Views;

/// <summary>A list line for tracks, queue entries and mixed search results.</summary>
public sealed partial class MediaRow : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(ListRow), typeof(MediaRow), new PropertyMetadata(null, (d, _) => ((MediaRow)d).Apply()));

    private static readonly Brush TransparentBrush = new SolidColorBrush(Colors.Transparent);
    private bool _hover;
    private bool _focus;

    public MediaRow() => InitializeComponent();

    public event EventHandler<RowAction>? PlayRequested;

    public event EventHandler<RowAction>? MenuRequested;

    public ListRow? Row
    {
        get => (ListRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    private void Apply()
    {
        if (Row is not { } row)
        {
            return;
        }

        TitleText.Text = row.Title;
        TitleText.FontWeight = row.Weight;
        TitleText.Foreground = (Brush)Application.Current.Resources[row.IsCurrent ? "AuroraAccent" : "AuroraText"];
        SubtitleText.Text = row.Subtitle;
        ToolTipService.SetToolTip(TitleText, row.Title);
        AutomationProperties.SetName(this, string.IsNullOrEmpty(row.Subtitle) ? row.Title : $"{row.Title}, {row.Subtitle}");
        PositionText.Text = row.PositionLabel;
        PositionText.Visibility = row.IsCurrent ? Visibility.Collapsed : row.PositionVisibility;
        NowIcon.Visibility = row.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        DurationText.Text = row.DurationLabel;
        DurationText.Visibility = row.DurationVisibility;
        Placeholder.Visibility = row.Image is null ? Visibility.Visible : Visibility.Collapsed;
        ArtRect.Fill = row.Image is null ? null : new ImageBrush { ImageSource = row.Image, Stretch = Stretch.UniformToFill };
        var playable = row.Item is { IsPlayable: true };
        MenuButton.Visibility = playable ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.Visibility = playable ? Visibility.Visible : Visibility.Collapsed;
        UpdateState();
    }

    private void UpdateState()
    {
        var active = _hover || _focus;
        Root.Background = active ? (Brush)Application.Current.Resources["AuroraHover"] : TransparentBrush;
        PlayButton.Opacity = MenuButton.Opacity = active ? 1 : 0;
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
