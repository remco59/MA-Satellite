using MaSatellite.Core.Security;
using MaSatellite.Core.Session;
using MaSatellite.Host.Audio;
using MaSatellite.Host.Library;
using MaSatellite.Host.Orchestration;
using MaSatellite.Host.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Clipboard = global::Windows.ApplicationModel.DataTransfer.Clipboard;
using ConnectionMode = MaSatellite.Host.Settings.ConnectionMode;
using DataPackage = global::Windows.ApplicationModel.DataTransfer.DataPackage;

namespace MaSatellite.App.Views;

public sealed partial class SettingsWindow : Window
{
    private const string SystemDefaultLabel = "System default";

    private readonly SettingsStore _store;
    private readonly SatelliteHost _host;
    private readonly MaLibraryService _library;
    private readonly Func<bool, Task> _applyAutostart;
    private readonly DispatcherQueueTimer _timer;
    private IReadOnlyList<AudioDeviceInfo> _devices = [];

    internal SettingsWindow(SettingsStore store, IAudioDeviceCatalog devices, SatelliteHost host, MaLibraryService library, Func<bool, Task> applyAutostart)
    {
        _store = store;
        _host = host;
        _library = library;
        _applyAutostart = applyAutostart;
        InitializeComponent();
        AuroraWindowChrome.Apply(this);
        Title = "ma-satellite settings";
        AppWindow.Resize(new SizeInt32(900, 790));

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
        _host.PairingChanged += OnPairingChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _host.PairingChanged -= OnPairingChanged;
        };
    }

    private void Populate(AppSettings s)
    {
        PlayerNameBox.Text = s.PlayerName;
        HostBox.Text = s.MaHost;
        ModeBox.SelectedIndex = s.ConnectionMode == ConnectionMode.Advertise ? 0 : 1;
        ListenPortBox.Value = s.ListenPort;
        DelayBox.Value = s.StaticDelayMs;
        AutostartSwitch.IsOn = s.Autostart;
        MaPortBox.Value = s.MaWebPort;
        ApiSwitch.IsOn = s.LocalApiEnabled;
        ApiPortBox.Value = s.LocalApiPort;
        ProtocolBox.SelectedIndex = s.ProtocolMode switch
        {
            SendspinProtocolMode.Encrypted => 1,
            SendspinProtocolMode.Legacy => 2,
            _ => 0,
        };
        UnpairedSwitch.IsOn = s.AllowUnpairedAccess;
        UpdateTokenStatus();
        RefreshPairing();

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

        SecurityLine.Text = DescribeSecurity(s.Session);
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
            MaWebPort = ToInt(MaPortBox.Value, AppSettings.DefaultMaWebPort),
            LocalApiEnabled = ApiSwitch.IsOn,
            LocalApiPort = ToInt(ApiPortBox.Value, AppSettings.DefaultLocalApiPort),
            ProtocolMode = ProtocolBox.SelectedIndex switch
            {
                1 => SendspinProtocolMode.Encrypted,
                2 => SendspinProtocolMode.Legacy,
                _ => SendspinProtocolMode.Auto,
            },
            AllowUnpairedAccess = UnpairedSwitch.IsOn,
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

        if (!string.IsNullOrWhiteSpace(TokenBox.Password))
        {
            _library.SaveToken(TokenBox.Password);
            TokenBox.Password = string.Empty;
            UpdateTokenStatus();
        }

        await _applyAutostart(updated.Autostart);
        MessageBar.Severity = InfoBarSeverity.Success;
        MessageBar.Title = "Saved";
        MessageBar.Message = "Settings saved.";
        MessageBar.IsOpen = true;
    }

    private static string DescribeSecurity(SessionState session)
    {
        if (session.AwaitingApproval)
        {
            return "Encrypted · waiting for you to approve this player in Music Assistant";
        }

        if (session.Pairing)
        {
            return "Encrypted · pairing in progress";
        }

        return session.Security switch
        {
            SessionSecurity.EncryptedPaired => "Encrypted · paired server",
            SessionSecurity.EncryptedUnpaired => "Encrypted · server not paired",
            _ => string.Empty,
        };
    }

    private void OnPairingChanged() => DispatcherQueue.TryEnqueue(RefreshPairing);

    private void RefreshPairing()
    {
        PairingTokenBox.Text = _host.KeyStore.PairingToken();
        var selected = PairedBox.SelectedIndex;
        PairedBox.Items.Clear();
        foreach (var record in _host.KeyStore.Records)
        {
            PairedBox.Items.Add(new ComboBoxItem { Content = $"Server {record.ServerId[..Math.Min(8, record.ServerId.Length)]}…", Tag = record.ServerId });
        }

        PairedBox.SelectedIndex = PairedBox.Items.Count == 0 ? -1 : Math.Clamp(selected, 0, PairedBox.Items.Count - 1);
        UnpairButton.IsEnabled = PairedBox.Items.Count > 0;
        PairedStatus.Text = PairedBox.Items.Count == 0 ? "No server is paired." : string.Empty;
    }

    private void OnCopyPairingToken(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(_host.KeyStore.PairingToken());
        Clipboard.SetContent(package);
        PairedStatus.Text = "Pairing token copied.";
    }

    private void OnNewPairingToken(object sender, RoutedEventArgs e)
    {
        _host.KeyStore.RegeneratePairingPsk();
        RefreshPairing();
        PairedStatus.Text = "A new token was created; the previous one no longer works.";
    }

    private void OnUnpair(object sender, RoutedEventArgs e)
    {
        if (PairedBox.SelectedItem is ComboBoxItem { Tag: string serverId })
        {
            _host.KeyStore.RemoveRecord(serverId);
            RefreshPairing();
            PairedStatus.Text = "Server forgotten. Remove this player in Music Assistant too, or pair again.";
        }
    }

    private void UpdateTokenStatus()
    {
        TokenStatus.Text = _library.HasToken ? "A token is saved (encrypted for your Windows account)." : "No token saved.";
        RemoveTokenButton.IsEnabled = _library.HasToken;
    }

    private void OnRemoveToken(object sender, RoutedEventArgs e)
    {
        _library.SaveToken(string.Empty);
        UpdateTokenStatus();
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            TokenStatus.Text = "Enter the Music Assistant host first.";
            return;
        }

        TokenStatus.Text = "Testing…";
        try
        {
            var port = ToInt(MaPortBox.Value, AppSettings.DefaultMaWebPort);
            var version = await _library.TestAsync(host, port, TokenBox.Password);
            TokenStatus.Text = $"Connected to Music Assistant {version}.";
        }
        catch (MaSatellite.MaApi.MaException ex)
        {
            TokenStatus.Text = ex.Message;
        }
    }

    private static int ToInt(double value, int fallback) => double.IsNaN(value) ? fallback : (int)Math.Round(value);
}
