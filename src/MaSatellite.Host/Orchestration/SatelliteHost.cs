using MaSatellite.Core.Audio;
using MaSatellite.Core.Discovery;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using MaSatellite.Core.Time;
using MaSatellite.Core.Security;
using MaSatellite.Core.Volume;
using MaSatellite.Host.LocalApi;
using MaSatellite.Host.Settings;
using Microsoft.Extensions.Logging;

namespace MaSatellite.Host.Orchestration;

/// <summary>Everything non-UI that makes the player run: the Sendspin service wired to settings, volume and output.</summary>
public sealed class SatelliteHost : IAsyncDisposable, MaSatellite.Host.LocalApi.ILocalApiSource
{
    private readonly SettingsStore _settings;
    private readonly SendspinService _service;
    private readonly Action<string?> _setOutputDevice;
    private readonly IVolumeControl _volume;
    private readonly string _softwareVersion;
    private readonly ILogger<SatelliteHost> _logger;
    private readonly ILoggerFactory _loggers;
    private LocalApiServer? _api;
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private AppSettings _applied;
    private bool _started;

    public SatelliteHost(
        SettingsStore settings,
        IAudioOutput output,
        IVolumeControl volume,
        Action<string?> setOutputDevice,
        IServiceAdvertiser? advertiser,
        ILastServerStore lastServerStore,
        ILoggerFactory loggers,
        string softwareVersion,
        ILocalClock? clock = null,
        ISendspinKeyStore? keyStore = null)
    {
        _settings = settings;
        _setOutputDevice = setOutputDevice;
        _volume = volume;
        _softwareVersion = softwareVersion;
        _logger = loggers.CreateLogger<SatelliteHost>();
        _loggers = loggers;
        _applied = settings.Current;
        _service = new SendspinService(output, volume, clock, advertiser, lastServerStore, loggers, keyStore);
        _service.PairingCompleted += () => PairingChanged?.Invoke();
        _service.StaticDelayChanged += OnServerChangedStaticDelay;
        _service.StatusChanged += s => StatusChanged?.Invoke(s);
    }

    public SendspinService Service => _service;

    /// <summary>The system master volume of the output device, which is also MA's volume for this player.</summary>
    public IVolumeControl Volume => _volume;

    public SatelliteStatus Status => _service.Status;

    public long ServerNowMicros => _service.ServerNowMicros;

    public event Action<SatelliteStatus>? StatusChanged;

    /// <summary>Raised when a server paired with this player.</summary>
    public event Action? PairingChanged;

    /// <summary>Keys, pairing token and paired servers of the encrypted protocol.</summary>
    public ISendspinKeyStore KeyStore => _service.KeyStore;

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var s = _settings.Current;
        _applied = s;
        _setOutputDevice(s.OutputDeviceId);
        await _service.StartAsync(SettingsMapper.ToSendspinOptions(s, _softwareVersion), SettingsMapper.ToConnectionOptions(s)).ConfigureAwait(false);
        await ApplyLocalApiAsync(s).ConfigureAwait(false);
        _settings.Changed += OnSettingsChanged;
    }

    public async Task StopAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        await StopLocalApiAsync().ConfigureAwait(false);
        await _service.StopAsync().ConfigureAwait(false);
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _service.DisposeAsync().ConfigureAwait(false);
        _applyLock.Dispose();
    }

    private void OnSettingsChanged(object? sender, AppSettings updated) => _ = ApplyAsync(updated);

    private async Task ApplyAsync(AppSettings updated)
    {
        await _applyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var before = _applied;
            _applied = updated;
            if (before.OutputDeviceId != updated.OutputDeviceId)
            {
                _setOutputDevice(updated.OutputDeviceId);
            }

            if (SettingsMapper.RequiresReconnect(before, updated))
            {
                _logger.LogInformation("Settings changed; reconnecting");
                await _service.RestartAsync(SettingsMapper.ToSendspinOptions(updated, _softwareVersion), SettingsMapper.ToConnectionOptions(updated)).ConfigureAwait(false);
            }
            else if (before.StaticDelayMs != updated.StaticDelayMs)
            {
                await _service.SetStaticDelayAsync(updated.StaticDelayMs).ConfigureAwait(false);
            }

            if (before.LocalApiEnabled != updated.LocalApiEnabled || before.LocalApiPort != updated.LocalApiPort)
            {
                await ApplyLocalApiAsync(updated).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying settings failed");
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task ApplyLocalApiAsync(AppSettings s)
    {
        await StopLocalApiAsync().ConfigureAwait(false);
        if (!s.LocalApiEnabled)
        {
            return;
        }

        try
        {
            var api = new LocalApiServer(this, s.LocalApiPort, _loggers.CreateLogger<LocalApiServer>());
            api.Start();
            _api = api;
        }
        catch (Exception ex)
        {
            // Another program may own the port; the player must keep working without the widget API.
            _logger.LogWarning(ex, "Local API could not start on port {Port}", s.LocalApiPort);
        }
    }

    private async Task StopLocalApiAsync()
    {
        var api = _api;
        _api = null;
        if (api is not null)
        {
            await api.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnServerChangedStaticDelay(int delayMs)
    {
        var current = _settings.Current;
        if (current.StaticDelayMs != delayMs)
        {
            _applied = _applied with { StaticDelayMs = delayMs };
            _settings.Save(current with { StaticDelayMs = delayMs });
        }
    }

    /// <summary>Sends a controller command (play, pause, next, ...) to the server on behalf of the UI/API.</summary>
    public Task SendCommandAsync(ControllerCommand command) => _service.SendCommandAsync(command);
}
