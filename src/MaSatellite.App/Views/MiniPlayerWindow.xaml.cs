using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using MaSatellite.Host.Audio;
using MaSatellite.Host.Orchestration;
using MaSatellite.Host.Presentation;
using MaSatellite.Host.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace MaSatellite.App.Views;

/// <summary>The tray flyout: artwork, track info, transport, seek bar, volume and output device.</summary>
internal sealed partial class MiniPlayerWindow : Window
{
    private const int BaseWidth = 400;
    private const int BaseHeight = 430;
    private const string SystemDefaultLabel = "System default";

    private readonly SatelliteHost _host;
    private readonly SettingsStore _settings;
    private readonly IAudioDeviceCatalog _catalog;
    private readonly Action _openSettings;
    private readonly Func<Rectangle?> _trayRect;
    private readonly DispatcherQueueTimer _progressTimer;
    private readonly DispatcherQueueTimer _seekDebounce;
    private IReadOnlyList<AudioDeviceInfo> _devices = [];
    private bool _updating;
    private int _artworkVersion = -1;
    private long _hiddenAtTicks;
    private long _pendingSeekMs;

    public MiniPlayerWindow(SatelliteHost host, SettingsStore settings, IAudioDeviceCatalog catalog, Action openSettings, Func<Rectangle?> trayRect)
    {
        _host = host;
        _settings = settings;
        _catalog = catalog;
        _openSettings = openSettings;
        _trayRect = trayRect;
        InitializeComponent();
        Title = "ma-satellite";

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
        }

        AppWindow.IsShownInSwitchers = false;
        Activated += OnActivated;

        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
        _progressTimer.Tick += (_, _) => Refresh();

        _seekDebounce = DispatcherQueue.CreateTimer();
        _seekDebounce.Interval = TimeSpan.FromMilliseconds(300);
        _seekDebounce.IsRepeating = false;
        _seekDebounce.Tick += (_, _) => SendSeek();

        _host.Volume.Changed += OnSystemVolumeChanged;
        Closed += (_, _) =>
        {
            _progressTimer.Stop();
            _host.Volume.Changed -= OnSystemVolumeChanged;
        };
    }

    public bool IsShown { get; private set; }

    /// <summary>Shows the flyout, or hides it when it is already open. Ignores the click that merely dismissed it.</summary>
    public void Toggle()
    {
        if (IsShown)
        {
            HideFlyout();
        }
        else if (Environment.TickCount64 - _hiddenAtTicks > 300)
        {
            ShowFlyout();
        }
    }

    public void ShowFlyout()
    {
        LoadDevices();
        Refresh();
        Position();
        AppWindow.Show();
        Activate();
        _progressTimer.Start();
        IsShown = true;
    }

    public void HideFlyout()
    {
        _progressTimer.Stop();
        AppWindow.Hide();
        IsShown = false;
        _hiddenAtTicks = Environment.TickCount64;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && IsShown)
        {
            HideFlyout();
        }
    }

    private void Position()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var width = (int)(BaseWidth * scale);
        var height = (int)(BaseHeight * scale);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)(12 * scale);
        var x = area.X + area.Width - width - margin;
        var y = area.Y + area.Height - height - margin;
        if (_trayRect() is { } icon && icon.Width > 0)
        {
            x = Math.Clamp(icon.X + (icon.Width / 2) - (width / 2), area.X + margin, area.X + area.Width - width - margin);
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void LoadDevices()
    {
        try
        {
            _devices = _catalog.GetOutputDevices();
        }
        catch (Exception)
        {
            _devices = [];
        }

        _updating = true;
        DeviceBox.Items.Clear();
        DeviceBox.Items.Add(SystemDefaultLabel);
        foreach (var d in _devices)
        {
            DeviceBox.Items.Add(d.Name);
        }

        var index = _settings.Current.OutputDeviceId is { } id ? _devices.ToList().FindIndex(d => d.Id == id) + 1 : 0;
        DeviceBox.SelectedIndex = Math.Max(index, 0);
        _updating = false;
    }

    /// <summary>Called on the UI thread whenever the status changes (and every 500 ms while shown).</summary>
    public void Refresh()
    {
        var status = _host.Status;
        var session = status.Session;
        var md = session.Metadata;
        var view = TransportLogic.Describe(session);
        _updating = true;
        try
        {
            TitleText.Text = string.IsNullOrEmpty(md.Title) ? "Nothing playing" : md.Title;
            ArtistText.Text = md.Artist ?? string.Empty;
            AlbumText.Text = md.Album ?? string.Empty;
            StatusText.Text = status.Connection switch
            {
                ConnectionState.Connected => $"{(view.IsPlaying ? "Playing on" : "Connected to")} {status.ServerName}" + (string.IsNullOrEmpty(session.Group.GroupName) ? string.Empty : $" · {session.Group.GroupName}"),
                ConnectionState.Listening => status.Detail ?? "Waiting for Music Assistant",
                ConnectionState.Connecting => status.Detail ?? "Connecting…",
                _ => "Stopped",
            };

            PlayPauseIcon.Glyph = view.IsPlaying ? "" : "";
            PlayPauseButton.IsEnabled = view.CanPlayPause;
            PrevButton.IsEnabled = view.CanPrevious;
            NextButton.IsEnabled = view.CanNext;

            var duration = md.Progress?.TrackDurationMs ?? 0;
            var position = md.PositionMs(_host.ServerNowMicros) ?? 0;
            SeekSlider.IsEnabled = view.CanSeek && duration > 0;
            SeekSlider.Maximum = Math.Max(duration, 1);
            if (!_seekDebounce.IsRunning)
            {
                SeekSlider.Value = Math.Min(position, SeekSlider.Maximum);
            }

            ElapsedText.Text = ProgressFormatter.Time(position);
            TotalText.Text = duration > 0 ? ProgressFormatter.Time(duration) : "—";

            VolumeSlider.Value = status.Volume;
            VolumeText.Text = status.Volume.ToString();
            MuteIcon.Glyph = status.Muted || status.Volume == 0 ? "" : "";

            UpdateArtwork(session.Artwork);
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateArtwork(ArtworkImage? artwork)
    {
        if (artwork is null)
        {
            ArtworkImage.Source = null;
            _artworkVersion = -1;
            return;
        }

        if (artwork.Version == _artworkVersion)
        {
            return;
        }

        _artworkVersion = artwork.Version;
        _ = LoadArtworkAsync(artwork);
    }

    private async Task LoadArtworkAsync(ArtworkImage artwork)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(artwork.Data.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 192 };
            await bitmap.SetSourceAsync(stream);
            if (artwork.Version == _artworkVersion)
            {
                ArtworkImage.Source = bitmap;
            }
        }
        catch (Exception)
        {
            ArtworkImage.Source = null;
        }
    }

    private void OnSystemVolumeChanged() => DispatcherQueue.TryEnqueue(Refresh);

    private async void OnPlayPause(object sender, RoutedEventArgs e) => await Send(TransportLogic.PlayPause(_host.Status.Session));

    private async void OnPrevious(object sender, RoutedEventArgs e) => await Send(TransportLogic.Previous(_host.Status.Session));

    private async void OnNext(object sender, RoutedEventArgs e) => await Send(TransportLogic.Next(_host.Status.Session));

    private async Task Send(ControllerCommand? command)
    {
        if (command is not null)
        {
            await _host.SendCommandAsync(command);
        }
    }

    private void OnSeekChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _pendingSeekMs = (long)e.NewValue;
        ElapsedText.Text = ProgressFormatter.Time(_pendingSeekMs);
        _seekDebounce.Stop();
        _seekDebounce.Start();
    }

    private async void SendSeek() => await Send(TransportLogic.Seek(_host.Status.Session, _pendingSeekMs));

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        VolumeText.Text = ((int)e.NewValue).ToString();
        _host.Volume.SetVolume((int)e.NewValue);
    }

    private void OnMute(object sender, RoutedEventArgs e) => _host.Volume.SetMuted(!_host.Volume.Muted);

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || DeviceBox.SelectedIndex < 0)
        {
            return;
        }

        string? id = DeviceBox.SelectedIndex > 0 && DeviceBox.SelectedIndex - 1 < _devices.Count ? _devices[DeviceBox.SelectedIndex - 1].Id : null;
        if (id != _settings.Current.OutputDeviceId)
        {
            _settings.Save(_settings.Current with { OutputDeviceId = id });
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _openSettings();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
