using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace MaSatellite.App.Views;

/// <summary>Keep native Windows caption buttons visually consistent with the dark app.</summary>
internal static class AuroraWindowChrome
{
    /// <summary>Gives a window the app icon so the taskbar and title bar do not show the generic placeholder.</summary>
    public static void SetIcon(Window window)
    {
        try
        {
            window.AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"));
        }
        catch (Exception)
        {
            // A missing icon file must never prevent a window from opening.
        }
    }

    public static void Apply(Window window, UIElement? dragRegion = null)
    {
        SetIcon(window);

        // The library window draws its own title area; the caption buttons then sit on top of the page.
        if (dragRegion is not null && AppWindowTitleBar.IsCustomizationSupported())
        {
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(dragRegion);
        }

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var bar = window.AppWindow.TitleBar;
        var background = dragRegion is not null ? Colors.Transparent : Color.FromArgb(255, 10, 10, 18);
        var foreground = Color.FromArgb(255, 244, 243, 250);
        bar.BackgroundColor = background;
        bar.ForegroundColor = foreground;
        bar.InactiveBackgroundColor = background;
        bar.InactiveForegroundColor = Color.FromArgb(255, 156, 153, 180);
        bar.ButtonBackgroundColor = background;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveBackgroundColor = background;
        bar.ButtonInactiveForegroundColor = Color.FromArgb(255, 156, 153, 180);
        bar.ButtonHoverBackgroundColor = Color.FromArgb(255, 38, 36, 66);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = Color.FromArgb(255, 54, 50, 92);
        bar.ButtonPressedForegroundColor = foreground;
    }
}
