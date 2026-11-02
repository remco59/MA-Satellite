namespace MaSatellite.Windows.Tray;

/// <summary>
/// Decides when a tray hover preview should open or close. Uses caller-provided timestamps
/// and hit tests so the behaviour can be tested without a Windows message loop.
/// </summary>
public sealed class TrayHoverController
{
    internal const int OpenDelayMs = 350;
    internal const int CloseDelayMs = 500;

    private long? _hoverStartedAt;
    private long? _outsideStartedAt;
    private bool _waitForIconExit;

    public TrayHoverAction Update(long nowMs, bool overIcon, bool overPlayer, bool playerVisible, bool hoverPreview)
    {
        if (!overIcon)
        {
            _hoverStartedAt = null;
            _waitForIconExit = false;
        }

        if (playerVisible && hoverPreview)
        {
            if (overIcon || overPlayer)
            {
                _outsideStartedAt = null;
            }
            else
            {
                _outsideStartedAt ??= nowMs;
                if (nowMs - _outsideStartedAt >= CloseDelayMs)
                {
                    _outsideStartedAt = null;
                    return TrayHoverAction.Hide;
                }
            }
        }
        else
        {
            _outsideStartedAt = null;
        }

        if (playerVisible || !overIcon || _waitForIconExit)
        {
            return TrayHoverAction.None;
        }

        _hoverStartedAt ??= nowMs;
        if (nowMs - _hoverStartedAt < OpenDelayMs)
        {
            return TrayHoverAction.None;
        }

        _hoverStartedAt = null;
        _waitForIconExit = true;
        return TrayHoverAction.Show;
    }

    /// <summary>A click must not immediately reopen the player while the pointer remains on the icon.</summary>
    public void SuppressUntilIconExit()
    {
        _hoverStartedAt = null;
        _outsideStartedAt = null;
        _waitForIconExit = true;
    }
}

public enum TrayHoverAction
{
    None,
    Show,
    Hide,
}
