using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace MaSatellite.App.Views;

/// <summary>Small, cheap motion helpers. Everything is skipped when Windows has animations turned off.</summary>
internal static class Motion
{
    private static readonly Lazy<bool> AnimationsEnabled = new(() =>
    {
        try { return new UISettings().AnimationsEnabled; }
        catch (Exception) { return true; }
    });

    public static bool Enabled => AnimationsEnabled.Value;

    /// <summary>Fades an element from transparent to opaque with the compositor (no UI-thread work per frame).</summary>
    public static void FadeIn(UIElement element, int milliseconds = 240)
    {
        if (!Enabled)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 0f);
        animation.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation("Opacity", animation);
    }

    /// <summary>A slow breathing opacity loop for loading skeletons. Runs on the compositor.</summary>
    public static void Pulse(UIElement element)
    {
        if (!Enabled)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 0.55f);
        animation.InsertKeyFrame(1f, 1f);
        animation.Duration = TimeSpan.FromMilliseconds(900);
        animation.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
        animation.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", animation);
    }

    /// <summary>Ease-out interpolation driven by a dispatcher timer, for properties the compositor cannot animate (e.g. column widths).</summary>
    public static DispatcherQueueTimer? Tween(DispatcherQueue queue, double from, double to, int milliseconds,
        Action<double> apply, Action? done = null)
    {
        if (!Enabled || milliseconds <= 0)
        {
            apply(to);
            done?.Invoke();
            return null;
        }

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        var started = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp((DateTime.UtcNow - started).TotalMilliseconds / milliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            apply(from + ((to - from) * eased));
            if (t >= 1)
            {
                timer.Stop();
                done?.Invoke();
            }
        };
        timer.Start();
        return timer;
    }

    /// <summary>All descendants of <paramref name="root"/> in the visual tree.</summary>
    public static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element)
            {
                yield return element;
            }

            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
