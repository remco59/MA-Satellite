using MaSatellite.Host.Presentation;

namespace MaSatellite.Host.Tests;

public class NavigationHistoryTests
{
    [Fact]
    public void Starts_without_back_or_forward()
    {
        var h = new NavigationHistory<string>("home");
        Assert.False(h.CanGoBack);
        Assert.False(h.CanGoForward);
        Assert.Equal("home", h.Current);
    }

    [Fact]
    public void Back_and_forward_walk_the_stack()
    {
        var h = new NavigationHistory<string>("home");
        h.Navigate("album");
        h.Navigate("artist");
        Assert.Equal("album", h.GoBack());
        Assert.Equal("home", h.GoBack());
        Assert.False(h.CanGoBack);
        Assert.Equal("album", h.GoForward());
        Assert.True(h.CanGoForward);
    }

    [Fact]
    public void Navigating_after_back_clears_forward()
    {
        var h = new NavigationHistory<string>("home");
        h.Navigate("album");
        h.GoBack();
        h.Navigate("queue");
        Assert.False(h.CanGoForward);
        Assert.Equal("home", h.GoBack());
    }

    [Fact]
    public void Navigating_to_the_current_place_is_a_no_op()
    {
        var h = new NavigationHistory<string>("home");
        Assert.False(h.Navigate("home"));
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void Replace_does_not_add_history()
    {
        var h = new NavigationHistory<string>("search");
        h.Replace("search2");
        Assert.False(h.CanGoBack);
        Assert.Equal("search2", h.Current);
    }

    [Fact]
    public void Going_past_the_ends_stays_put()
    {
        var h = new NavigationHistory<string>("home");
        Assert.Equal("home", h.GoBack());
        Assert.Equal("home", h.GoForward());
    }
}
