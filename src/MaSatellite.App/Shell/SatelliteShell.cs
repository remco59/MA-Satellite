using MaSatellite.App.Views;
using MaSatellite.Host.Autostart;
using MaSatellite.Host.Infrastructure;
using MaSatellite.Host.Logging;
using MaSatellite.Host.Settings;
using MaSatellite.Windows.Autostart;
using MaSatellite.Windows.Tray;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace MaSatellite.App.Shell;

/// <summary>Owns the tray icon and the app's top-level windows. The app has no main window; it lives in the tray.</summary>
internal sealed class SatelliteShell : IDisposable
{
    private const int CmdOpen = 1, CmdSettings = 2, CmdAutostart = 3, CmdExit = 99;

    private readonly Action _exit;
    private readonly AppPaths _paths = new();
    private ILoggerFactory? _loggers;
    private ILogger<SatelliteShell>? _logger;
    private SettingsStore? _settings;
    private AutostartReconciler? _autostart;
    private IAutostartService? _autostartService;
    private AutostartState _autostartState = AutostartState.Unavailable;
    private TrayIcon? _tray;
    private Window? _keepAlive;
    private SettingsWindow? _settingsWindow;

    public SatelliteShell(Action exit)
    {
        _exit = exit;
    }

    public void Start()
    {
        _loggers = LoggingSetup.Create(_paths);
        _logger = _loggers.CreateLogger<SatelliteShell>();
        _logger.LogInformation("ma-satellite {Version} starting", typeof(SatelliteShell).Assembly.GetName().Version);

        _settings = new SettingsStore(_paths, _loggers.CreateLogger<SettingsStore>());
        var settings = _settings.Load();

        // A window that is never shown keeps the WinUI process alive while only the tray icon is visible.
        _keepAlive = new Window();

        _autostartService = new PackagedAutostartService(_loggers.CreateLogger<PackagedAutostartService>());
        _autostart = new AutostartReconciler(_autostartService, _loggers.CreateLogger<AutostartReconciler>());
        _ = ReconcileAutostartAsync(settings.Autostart);

        _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"))
        {
            MenuProvider = BuildMenu,
        };
        _tray.MenuItemInvoked += OnMenuItem;
        _tray.Clicked += ShowSettings;
        _tray.Show();
        _tray.SetTooltip($"ma-satellite — {settings.PlayerName}");
    }

    public void Dispose()
    {
        _tray?.Dispose();
        _keepAlive?.Close();
        _loggers?.Dispose();
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu() =>
    [
        new TrayMenuItem(CmdOpen, "Open"),
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

    private void ShowSettings()
    {
        if (_settings is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplyAutostartAsync);
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
