using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MaSatellite.App.Views;

/// <summary>
/// Horizontal row of cards. It scrolls only with its chevron buttons; the mouse wheel is
/// passed on to the page so scrolling down Home never gets stuck on a row.
/// </summary>
public sealed partial class Shelf : UserControl
{
    private ScrollViewer? _scroller;

    public Shelf()
    {
        InitializeComponent();
        Items.Loaded += (_, _) => AttachScroller();
        Items.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnWheel), handledEventsToo: true);
    }

    public event ItemClickEventHandler? ItemClick;

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Subtitle
    {
        get => SubtitleText.Text;
        set
        {
            SubtitleText.Text = value;
            SubtitleText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public object? ItemsSource
    {
        get => Items.ItemsSource;
        set
        {
            Items.ItemsSource = value;
            _scroller?.ChangeView(0, null, null, disableAnimation: true);
            UpdateButtons();
        }
    }

    public DataTemplate? ItemTemplate
    {
        get => Items.ItemTemplate;
        set => Items.ItemTemplate = value;
    }

    private void AttachScroller()
    {
        if (_scroller is not null)
        {
            return;
        }

        _scroller = FindScroller(Items);
        if (_scroller is not null)
        {
            _scroller.ViewChanged += (_, _) => UpdateButtons();
            // Items load and resize after layout; keep the chevrons' enabled state in step.
            Items.LayoutUpdated += (_, _) => UpdateButtons();
        }

        UpdateButtons();
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScroller(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void UpdateButtons()
    {
        if (_scroller is null)
        {
            return;
        }

        PreviousButton.IsEnabled = _scroller.HorizontalOffset > 1;
        NextButton.IsEnabled = _scroller.HorizontalOffset < _scroller.ScrollableWidth - 1;
    }

    private void Scroll(int direction)
    {
        if (_scroller is null)
        {
            return;
        }

        // Move by roughly one screenful, keeping a card of overlap for context.
        var step = Math.Max(200, _scroller.ViewportWidth - 200);
        var target = Math.Clamp(_scroller.HorizontalOffset + (direction * step), 0, _scroller.ScrollableWidth);
        _scroller.ChangeView(target, null, null);
    }

    private void OnPrevious(object sender, RoutedEventArgs e) => Scroll(-1);

    private void OnNext(object sender, RoutedEventArgs e) => Scroll(1);

    private void OnItemClicked(object sender, ItemClickEventArgs e) => ItemClick?.Invoke(this, e);

    private void OnItemsSizeChanged(object sender, SizeChangedEventArgs e) => UpdateButtons();

    /// <summary>Hand wheel input to the enclosing page scroller instead of moving this row.</summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        DependencyObject? parent = this;
        while (parent is not null && parent is not ScrollViewer)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        if (parent is ScrollViewer page)
        {
            var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            page.ChangeView(null, Math.Clamp(page.VerticalOffset - delta, 0, page.ScrollableHeight), null, disableAnimation: true);
        }

        e.Handled = true;
    }
}
