using MaSatellite.App.Views;
using MaSatellite.Core.Discovery;
using MaSatellite.Core.Session;
using MaSatellite.Core.Time;
using MaSatellite.Host.Audio;
using MaSatellite.Host.Autostart;
using MaSatellite.Host.Infrastructure;
using MaSatellite.Host.Library;
using MaSatellite.Host.Logging;
using MaSatellite.Host.Orchestration;
using MaSatellite.Host.Security;
using MaSatellite.Host.Settings;
using MaSatellite.Windows.Audio;
using MaSatellite.Windows.Autostart;
using MaSatellite.Windows.Media;
using MaSatellite.Windows.Security;
using MaSatellite.Windows.Tray;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MaSatellite.App.Shell;

/// <summary>Owns the tray icon, the player host and the app's top-level windows. The app has no main window; it lives in the tray.</summary>
internal sealed class SatelliteShell : IDisposable
{
    private const int CmdOpen = 1, CmdSettings = 2, CmdAutostart = 3, CmdLibrary = 4, CmdExit = 99;

    private readonly Action _exit;
    private readonly AppPaths _paths = new();
    private ILoggerFactory? _loggers;
    private ILogger<SatelliteShell>? _logger;
    private SettingsStore? _settings;
    private AutostartReconciler? _autostart;
    private AutostartState _autostartState = AutostartState.Unavailable;
    private TrayIcon? _tray;
    private Window? _keepAlive;
    private SettingsWindow? _settingsWindow;
    private MiniPlayerWindow? _miniPlayer;
    private LibraryWindow? _libraryWindow;
    private MaLibraryService? _library;
    private LibraryController? _libraryController;
    private SmtcController? _smtc;
    private DispatcherQueue? _ui;
    private WasapiAudioOutput? _output;
    private WindowsMasterVolume? _volume;
    private SatelliteHost? _host;
    private readonly IAudioDeviceCatalog _devices = new WasapiDeviceCatalog();
    private bool _disposed;

    public SatelliteShell(Action exit)
    {
        _exit = exit;
    }

    public void Start()
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _loggers = LoggingSetup.Create(_paths);
        _logger = _loggers.CreateLogger<SatelliteShell>();
        var version = typeof(SatelliteShell).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _logger.LogInformation("ma-satellite {Version} starting", version);

        _settings = new SettingsStore(_paths, _loggers.CreateLogger<SettingsStore>());
        var settings = _settings.Load();

        // A window that is never shown keeps the WinUI process alive while only the tray icon is visible.
        _keepAlive = new Window();

        var autostartService = AutostartServiceFactory.Create(_loggers, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MaSatellite.App.exe"));
        _autostart = new AutostartReconciler(autostartService, _loggers.CreateLogger<AutostartReconciler>());
        _ = ReconcileAutostartAsync(settings.Autostart);

        _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"))
        {
            MenuProvider = BuildMenu,
        };
        _tray.MenuItemInvoked += OnMenuItem;
        _tray.Clicked += ToggleMiniPlayer;
        _tray.Show();
        _tray.SetTooltip($"ma-satellite — {settings.PlayerName}");

        StartPlayer(version, settings);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tray?.Dispose();
        _miniPlayer?.Close();
        _miniPlayer = null;
        _libraryWindow?.Close();
        _libraryWindow = null;
        try
        {
            // Tells the server we are leaving (client/goodbye) and releases the audio device.
            _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Stopping the player failed");
        }

        _library?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _smtc?.Dispose();
        _volume?.Dispose();
        _output?.Dispose();
        _keepAlive?.Close();
        _loggers?.Dispose();
    }

    private void StartPlayer(string version, AppSettings settings)
    {
        var loggers = _loggers!;
        try
        {
            var output = new WasapiAudioOutput(StopwatchClock.Instance, settings.OutputDeviceId, loggers.CreateLogger<WasapiAudioOutput>());
            _output = output;
            var volume = new WindowsMasterVolume(settings.OutputDeviceId, loggers.CreateLogger<WindowsMasterVolume>());
            _volume = volume;
            var secrets = new DpapiSecretStore(_paths, loggers.CreateLogger<DpapiSecretStore>());
            var host = new SatelliteHost(
                _settings!,
                output,
                volume,
                deviceId =>
                {
                    output.DeviceId = deviceId;
                    volume.SetDevice(deviceId);
                },
                new MdnsAdvertiser(loggers.CreateLogger<MdnsAdvertiser>()),
                new FileLastServerStore(_paths),
                loggers,
                version,
                keyStore: new SecretKeyStore(secrets));
            _host = host;
            var library = new MaLibraryService(_settings!, secrets, loggers.CreateLogger<MaLibraryService>());
            _library = library;
            _libraryController = new LibraryController(library, _settings!);
            host.StatusChanged += OnStatusChanged;
            var smtc = new SmtcController(loggers.CreateLogger<SmtcController>());
            _smtc = smtc;
            smtc.CommandRequested += command => _ = host.SendCommandAsync(command);
            _ = StartHostAsync(host);
        }
        catch (Exception ex)
        {
            _logger!.LogError(ex, "Could not start the player");
            _tray?.SetTooltip("ma-satellite — could not start (see log)");
        }
    }

    private async Task StartHostAsync(SatelliteHost host)
    {
        try
        {
            await host.StartAsync();
        }
        catch (Exception ex)
        {
            _logger!.LogError(ex, "Starting the player failed");
        }
    }

    private void OnStatusChanged(SatelliteStatus status)
    {
        _ui?.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            _tray?.SetTooltip(FormatTooltip(status));
            _smtc?.Update(status.Session, status.Connection == ConnectionState.Connected, _host?.ServerNowMicros ?? 0);
            if (_miniPlayer is { IsShown: true })
            {
                _miniPlayer.Refresh();
            }

            // The main library window also needs Sendspin state updates (not just MA queue events).
            _libraryWindow?.RefreshLocalPlayback();
        });
    }

    internal static string FormatTooltip(SatelliteStatus s)
    {
        var name = "ma-satellite";
        var title = s.Session.Metadata.Title;
        return s.Connection switch
        {
            ConnectionState.Connected when s.Session.AwaitingApproval => $"{name} — approve this player in Music Assistant",
            ConnectionState.Connected when s.Session.Pairing => $"{name} — pairing with {s.ServerName}…",
            ConnectionState.Connected when !string.IsNullOrEmpty(title) => $"{name} — {title}" + (string.IsNullOrEmpty(s.Session.Metadata.Artist) ? string.Empty : $" · {s.Session.Metadata.Artist}"),
            ConnectionState.Connected => $"{name} — connected to {s.ServerName}",
            ConnectionState.Listening => $"{name} — waiting for Music Assistant",
            ConnectionState.Connecting => $"{name} — connecting…",
            _ => $"{name} — stopped",
        };
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu() =>
    [
        new TrayMenuItem(CmdOpen, "Open"),
        new TrayMenuItem(CmdLibrary, "Library…"),
        new TrayMenuItem(CmdSettings, "Settings…"),
        TrayMenuItem.Separator,
        new TrayMenuItem(CmdAutostart, "Start with Windows", Checked: _autostartState.IsEnabled(), Enabled: _autostartState.CanBeChangedByApp()),
        TrayMenuItem.Separator,
        new TrayMenuItem(CmdExit, "Exit"),
    ];

    private void OnMenuItem(int id)
    {
        switch (id)
        {
            case CmdOpen:
                ToggleMiniPlayer();
                break;
            case CmdLibrary:
                ShowLibrary();
                break;
            case CmdSettings:
                ShowSettings();
                break;
            case CmdAutostart:
                _ = ToggleAutostartAsync();
                break;
            case CmdExit:
                _logger?.LogInformation("Exit requested from the tray menu");
                Dispose();
                _exit();
                break;
        }
    }

    private void ToggleMiniPlayer()
    {
        if (_settings is null || _host is null || _tray is null)
        {
            return;
        }

        _miniPlayer ??= new MiniPlayerWindow(_host, _settings, _devices, ShowSettings, ShowLibrary, () => _tray?.GetScreenRect());
        _miniPlayer.Toggle();
    }

    private void ShowLibrary()
    {
        if (_libraryController is null || _host is null)
        {
            return;
        }

        if (_libraryWindow is null)
        {
            _libraryWindow = new LibraryWindow(_libraryController, _host, ShowSettings);
            _libraryWindow.Closed += (_, _) => _libraryWindow = null;
        }

        _libraryWindow.Activate();
    }

    private void ShowSettings()
    {
        if (_settings is null || _host is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, _devices, _host, _library!, ApplyAutostartAsync);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Activate();
    }

    private async Task ReconcileAutostartAsync(bool desired)
    {
        try
        {
            _autostartState = await _autostart!.ReconcileAsync(desired);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not reconcile autostart");
        }
    }

    private async Task ToggleAutostartAsync()
    {
        if (_settings is null)
        {
            return;
        }

        var desired = !_autostartState.IsEnabled();
        await ApplyAutostartAsync(desired);
        _settings.Save(_settings.Current with { Autostart = _autostartState.IsEnabled() });
    }

    private async Task ApplyAutostartAsync(bool desired)
    {
        await ReconcileAutostartAsync(desired);
    }
}
