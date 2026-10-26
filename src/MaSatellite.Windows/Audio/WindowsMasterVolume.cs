using MaSatellite.Core.Volume;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MaSatellite.Windows.Audio;

/// <summary>
/// MA's volume slider is the Windows master volume of the output device, in both directions: server commands set the
/// endpoint volume and changes made in Windows (flyout, keyboard, other apps) raise <see cref="Changed"/>.
/// </summary>
public sealed class WindowsMasterVolume : IVolumeControl, IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private string? _deviceId;
    private MMDevice? _device;
    private AudioEndpointVolume? _endpoint;
    private int _lastVolume = -1;
    private bool _lastMuted;
    private bool _disposed;

    public WindowsMasterVolume(string? deviceId = null, ILogger? logger = null)
    {
        _deviceId = deviceId;
        _logger = logger ?? NullLogger.Instance;
        _enumerator.RegisterEndpointNotificationCallback(this);
        Bind();
    }

    public event Action? Changed;

    public int Volume
    {
        get
        {
            lock (_gate)
            {
                return _endpoint is null ? 0 : VolumeMapper.ToPercent(_endpoint.MasterVolumeLevelScalar);
            }
        }
    }

    public bool Muted
    {
        get
        {
            lock (_gate)
            {
                return _endpoint?.Mute ?? false;
            }
        }
    }

    /// <summary>Follow a different output device (null = the system default).</summary>
    public void SetDevice(string? deviceId)
    {
        lock (_gate)
        {
            if (_deviceId == deviceId)
            {
                return;
            }

            _deviceId = deviceId;
        }

        Bind();
    }

    public void SetVolume(int volume)
    {
        lock (_gate)
        {
            if (_endpoint is not null)
            {
                _endpoint.MasterVolumeLevelScalar = VolumeMapper.ToScalar(volume);
            }
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            if (_endpoint is not null)
            {
                _endpoint.Mute = muted;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Unbind();
        }

        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }

    private void Bind()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Unbind();
            try
            {
                _device = _deviceId is null
                    ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : _enumerator.GetDevice(_deviceId);
                _endpoint = _device.AudioEndpointVolume;
                _endpoint.OnVolumeNotification += OnNotification;
                _lastVolume = VolumeMapper.ToPercent(_endpoint.MasterVolumeLevelScalar);
                _lastMuted = _endpoint.Mute;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not bind to the audio endpoint volume (device {Device})", _deviceId ?? "default");
                _device = null;
                _endpoint = null;
            }
        }

        Changed?.Invoke();
    }

    private void Unbind()
    {
        if (_endpoint is not null)
        {
            _endpoint.OnVolumeNotification -= OnNotification;
        }

        _endpoint = null;
        _device?.Dispose();
        _device = null;
    }

    private void OnNotification(AudioVolumeNotificationData data)
    {
        var volume = VolumeMapper.ToPercent(data.MasterVolume);
        bool changed;
        lock (_gate)
        {
            changed = volume != _lastVolume || data.Muted != _lastMuted;
            _lastVolume = volume;
            _lastMuted = data.Muted;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia && _deviceId is null)
        {
            Bind();
        }
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (deviceId == _deviceId)
        {
            Bind();
        }
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
    {
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }
}
