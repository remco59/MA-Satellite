using MaSatellite.Windows.Tray;

namespace MaSatellite.Windows.Tests;

public sealed class TrayHoverControllerTests
{
    [Fact]
    public void Opens_only_after_dwell_and_requires_icon_exit_before_reopening()
    {
        var hover = new TrayHoverController();
        Assert.Equal(TrayHoverAction.None, hover.Update(0, true, false, false, false));
        Assert.Equal(TrayHoverAction.None, hover.Update(349, true, false, false, false));
        Assert.Equal(TrayHoverAction.Show, hover.Update(350, true, false, false, false));
        Assert.Equal(TrayHoverAction.None, hover.Update(900, true, false, false, false));

        hover.Update(1000, false, false, false, false);
        Assert.Equal(TrayHoverAction.None, hover.Update(1100, true, false, false, false));
        Assert.Equal(TrayHoverAction.Show, hover.Update(1450, true, false, false, false));
    }

    [Fact]
    public void Moving_from_icon_to_player_does_not_close_preview()
    {
        var hover = new TrayHoverController();
        hover.Update(0, true, false, false, false);
        Assert.Equal(TrayHoverAction.Show, hover.Update(350, true, false, false, false));
        Assert.Equal(TrayHoverAction.None, hover.Update(450, false, true, true, true));
        Assert.Equal(TrayHoverAction.None, hover.Update(1500, false, true, true, true));
        Assert.Equal(TrayHoverAction.None, hover.Update(1600, false, false, true, true));
        Assert.Equal(TrayHoverAction.None, hover.Update(2099, false, false, true, true));
        Assert.Equal(TrayHoverAction.Hide, hover.Update(2100, false, false, true, true));
    }

    [Fact]
    public void Returning_to_preview_during_grace_period_cancels_dismissal()
    {
        var hover = new TrayHoverController();
        hover.Update(0, false, false, true, true);
        hover.Update(400, false, true, true, true);
        Assert.Equal(TrayHoverAction.None, hover.Update(600, false, false, true, true));
        Assert.Equal(TrayHoverAction.None, hover.Update(1000, false, true, true, true));
        Assert.Equal(TrayHoverAction.None, hover.Update(1300, false, false, true, true));
        Assert.Equal(TrayHoverAction.None, hover.Update(1700, false, false, true, true));
        Assert.Equal(TrayHoverAction.Hide, hover.Update(1800, false, false, true, true));
    }

    [Fact]
    public void Pinned_player_is_never_dismissed_by_hover()
    {
        var hover = new TrayHoverController();
        Assert.Equal(TrayHoverAction.None, hover.Update(0, false, false, true, false));
        Assert.Equal(TrayHoverAction.None, hover.Update(2000, false, false, true, false));
    }

    [Fact]
    public void Manual_click_suppresses_hover_until_cursor_leaves_icon()
    {
        var hover = new TrayHoverController();
        hover.Update(0, true, false, false, false);
        hover.SuppressUntilIconExit();
        Assert.Equal(TrayHoverAction.None, hover.Update(1000, true, false, false, false));
        hover.Update(1100, false, false, false, false);
        Assert.Equal(TrayHoverAction.None, hover.Update(1200, true, false, false, false));
        Assert.Equal(TrayHoverAction.Show, hover.Update(1550, true, false, false, false));
    }
}
