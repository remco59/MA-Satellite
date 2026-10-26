using System.Drawing;
using System.Runtime.InteropServices;
using static MaSatellite.Windows.Tray.NativeMethods;

namespace MaSatellite.Windows.Tray;

/// <summary>
/// A notification-area icon built directly on Shell_NotifyIcon with a hidden message-only window.
/// Must be created and used on a thread that pumps messages (the UI thread).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;

    private readonly string _iconPath;
    private readonly string _className = "MaSatellite.TrayWindow." + Guid.NewGuid().ToString("N");
    private readonly WndProc _wndProc; // kept alive for the lifetime of the window
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private string _tooltip = "ma-satellite";
    private bool _added;
    private bool _disposed;

    public TrayIcon(string iconPath)
    {
        _iconPath = iconPath;
        _wndProc = WindowProc;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>Builds the menu each time it is about to be shown, so checkmarks always reflect current state.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    /// <summary>Called with the id of the chosen menu item.</summary>
    public event Action<int>? MenuItemInvoked;

    /// <summary>Primary-click on the icon.</summary>
    public event Action? Clicked;

    public void Show()
    {
        ThrowIfDisposed();
        EnsureWindow();
        _hIcon = LoadImage(IntPtr.Zero, _iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        AddIcon();
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (_added)
        {
            var data = NewData(NIF_TIP);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    /// <summary>Screen rectangle of the icon, used to anchor the flyout. Null if the shell cannot report it.</summary>
    public Rectangle? GetScreenRect()
    {
        if (!_added)
        {
            return null;
        }

        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = _hwnd, uID = IconId };
        return Shell_NotifyIconGetRect(ref id, out var rect) == 0
            ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            return;
        }

        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            throw new InvalidOperationException("Could not register tray window class.", new System.ComponentModel.Win32Exception());
        }

        _hwnd = CreateWindowEx(0, _className, "ma-satellite tray", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not create tray window.", new System.ComponentModel.Win32Exception());
        }
    }

    private void AddIcon()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP:
                    Clicked?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    ShowContextMenu();
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == _taskbarCreatedMessage && !_disposed)
        {
            // Explorer restarted; the icon must be added again.
            AddIcon();
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var items = MenuProvider?.Invoke();
        if (items is null || items.Count == 0)
        {
            return;
        }

        var menu = CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }

                var flags = MF_STRING | (item.Checked ? MF_CHECKED : 0) | (item.Enabled ? 0 : MF_GRAYED);
                AppendMenu(menu, flags, (UIntPtr)(uint)item.Id, item.Text);
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd); // required so the menu closes when the user clicks elsewhere
            var chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0)
            {
                MenuItemInvoked?.Invoke(chosen);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
