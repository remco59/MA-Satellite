using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace MaSatellite.App.Views;

/// <summary>Keep native Windows caption buttons visually consistent with the dark app.</summary>
internal static class AuroraWindowChrome
{
    public static void Apply(Window window)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var bar = window.AppWindow.TitleBar;
        var background = Color.FromArgb(255, 14, 21, 36);
        var foreground = Color.FromArgb(255, 237, 242, 255);
        bar.BackgroundColor = background;
        bar.ForegroundColor = foreground;
        bar.InactiveBackgroundColor = background;
        bar.InactiveForegroundColor = Color.FromArgb(255, 154, 170, 199);
        bar.ButtonBackgroundColor = background;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = Color.FromArgb(255, 39, 52, 77);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = Color.FromArgb(255, 57, 72, 103);
        bar.ButtonPressedForegroundColor = foreground;
    }
}
