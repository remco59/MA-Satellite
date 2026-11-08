using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MaSatellite.App.Views;

/// <summary>The two large playlist cards at the top of Home.</summary>
public sealed partial class FeaturedCard : UserControl
{
    private ListRow? _row;

    public FeaturedCard() => InitializeComponent();

    public event EventHandler<ListRow>? OpenRequested;

    public event EventHandler<ListRow>? PlayRequested;

    public event EventHandler<ListRow>? ShuffleRequested;

    public void Bind(ListRow row, string eyebrow, string? description)
    {
        _row = row;
        EyebrowText.Text = eyebrow.ToUpperInvariant();
        TitleText.Text = row.Title;
        DescriptionText.Text = description ?? string.Empty;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
        Backdrop.Fill = row.LargeImage is { } image
            ? new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill }
            : (Brush)Application.Current.Resources["AuroraHeroGradient"];
        AutomationProperties.SetName(SurfaceButton, $"{eyebrow}: {row.Title}");
        var playable = row.Item is { IsPlayable: true };
        PlayButton.IsEnabled = playable;
        ShuffleButton.IsEnabled = playable && ShuffleAvailable;
        Motion.FadeIn(Root, 280);
    }

    /// <summary>Shuffle needs a queue we can reorder; it is off for playback on this PC.</summary>
    public bool ShuffleAvailable
    {
        get => _shuffleAvailable;
        set
        {
            _shuffleAvailable = value;
            ShuffleButton.IsEnabled = value && _row?.Item is { IsPlayable: true };
            ToolTipService.SetToolTip(ShuffleButton, value ? "Shuffle play" : "Shuffle isn't available when playing on this PC");
        }
    }

    private bool _shuffleAvailable = true;

    private void OnOpen(object sender, RoutedEventArgs e) { if (_row is not null) OpenRequested?.Invoke(this, _row); }

    private void OnPlay(object sender, RoutedEventArgs e) { if (_row is not null) PlayRequested?.Invoke(this, _row); }

    private void OnShuffle(object sender, RoutedEventArgs e) { if (_row is not null) ShuffleRequested?.Invoke(this, _row); }
}
