using MaSatellite.Core.Session;
using MaSatellite.Host.Audio;
using MaSatellite.Host.Orchestration;
using MaSatellite.Host.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using ConnectionMode = MaSatellite.Host.Settings.ConnectionMode;

namespace MaSatellite.App.Views;

public sealed partial class SettingsWindow : Window
{
    private const string SystemDefaultLabel = "System default";

    private readonly SettingsStore _store;
    private readonly SatelliteHost _host;
    private readonly Func<bool, Task> _applyAutostart;
    private readonly DispatcherQueueTimer _timer;
    private IReadOnlyList<AudioDeviceInfo> _devices = [];

    internal SettingsWindow(SettingsStore store, IAudioDeviceCatalog devices, SatelliteHost host, Func<bool, Task> applyAutostart)
    {
        _store = store;
        _host = host;
        _applyAutostart = applyAutostart;
        InitializeComponent();
        Title = "ma-satellite settings";
        AppWindow.Resize(new SizeInt32(560, 820));

        try
        {
            _devices = devices.GetOutputDevices();
        }
        catch (Exception)
        {
            _devices = [];
        }

        Populate(store.Current);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
        Closed += (_, _) => _timer.Stop();
    }

    private void Populate(AppSettings s)
    {
        PlayerNameBox.Text = s.PlayerName;
        HostBox.Text = s.MaHost;
        ModeBox.SelectedIndex = s.ConnectionMode == ConnectionMode.Advertise ? 0 : 1;
        ListenPortBox.Value = s.ListenPort;
        DelayBox.Value = s.StaticDelayMs;
        AutostartSwitch.IsOn = s.Autostart;

        DeviceBox.Items.Clear();
        DeviceBox.Items.Add(SystemDefaultLabel);
        foreach (var d in _devices)
        {
            DeviceBox.Items.Add(d.IsDefault ? $"{d.Name} (default)" : d.Name);
        }

        var index = 0;
        if (s.OutputDeviceId is not null)
        {
            var i = _devices.ToList().FindIndex(d => d.Id == s.OutputDeviceId);
            index = i >= 0 ? i + 1 : 0;
        }

        DeviceBox.SelectedIndex = index;
    }

    private void RefreshStatus()
    {
        var s = _host.Status;
        StatusLine.Text = s.Connection switch
        {
            ConnectionState.Connected => $"Connected to {s.ServerName}",
            ConnectionState.Listening => s.Detail ?? "Waiting for Music Assistant",
            ConnectionState.Connecting => s.Detail ?? "Connecting…",
            _ => s.Detail ?? "Stopped",
        };

        var md = s.Session.Metadata;
        NowPlayingLine.Text = string.IsNullOrEmpty(md.Title)
            ? string.Empty
            : string.IsNullOrEmpty(md.Artist) ? md.Title : $"{md.Title} — {md.Artist}";

        var p = _host.Service.Playback;
        DiagnosticsLine.Text = s.Session.StreamActive
            ? $"{s.Session.Stream!.Codec} {s.Session.Stream.SampleRate} Hz · buffered {p.BufferedMs:0} ms · sync error {p.SyncErrorMicros / 1000.0:0.0} ms · {(p.InSync ? "in sync" : "not in sync")} · volume {s.Volume}%{(s.Muted ? " (muted)" : string.Empty)}"
            : $"Volume {s.Volume}%{(s.Muted ? " (muted)" : string.Empty)}";
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        string? deviceId = null;
        if (DeviceBox.SelectedIndex > 0 && DeviceBox.SelectedIndex - 1 < _devices.Count)
        {
            deviceId = _devices[DeviceBox.SelectedIndex - 1].Id;
        }

        var updated = _store.Current with
        {
            PlayerName = PlayerNameBox.Text.Trim(),
            MaHost = HostBox.Text.Trim(),
            ConnectionMode = ModeBox.SelectedIndex == 1 ? ConnectionMode.Connect : ConnectionMode.Advertise,
            ListenPort = ToInt(ListenPortBox.Value, AppSettings.DefaultListenPort),
            StaticDelayMs = ToInt(DelayBox.Value, 0),
            OutputDeviceId = deviceId,
            Autostart = AutostartSwitch.IsOn,
        };

        var errors = _store.Save(updated);
        if (errors.Count > 0)
        {
            MessageBar.Severity = InfoBarSeverity.Error;
            MessageBar.Title = "Not saved";
            MessageBar.Message = string.Join(Environment.NewLine, errors);
            MessageBar.IsOpen = true;
            return;
        }

        await _applyAutostart(updated.Autostart);
        MessageBar.Severity = InfoBarSeverity.Success;
        MessageBar.Title = "Saved";
        MessageBar.Message = "Settings saved.";
        MessageBar.IsOpen = true;
    }

    private static int ToInt(double value, int fallback) => double.IsNaN(value) ? fallback : (int)Math.Round(value);
}
