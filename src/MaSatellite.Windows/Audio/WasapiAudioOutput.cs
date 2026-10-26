using System.Runtime.InteropServices;
using MaSatellite.Core.Audio;
using MaSatellite.Core.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MaSatellite.Windows.Audio;

/// <summary>
/// WASAPI shared-mode, event-driven render loop in the device's mix format. We run the loop ourselves (instead of a
/// player abstraction) because the sync engine needs to know exactly when each frame becomes audible:
/// <c>now + queued frames + stream latency</c>.
/// </summary>
public sealed class WasapiAudioOutput : IAudioOutput, IMMNotificationClient
{
    private const int TargetBufferMs = 60;
    private const long BufferDurationHns = 2_000_000; // 200 ms device buffer; we only keep ~60 ms of it filled

    private readonly ILocalClock _clock;
    private readonly ILogger _logger;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();
    private string? _deviceId;
    private Thread? _thread;
    private ManualResetEvent? _stop;
    private bool _registered;

    public WasapiAudioOutput(ILocalClock clock, string? deviceId = null, ILogger? logger = null)
    {
        _clock = clock;
        _deviceId = deviceId;
        _logger = logger ?? NullLogger.Instance;
    }

    public event EventHandler<Exception?>? Failed;

    /// <summary>Output device id; null follows the system default. Changing it while running restarts the output.</summary>
    public string? DeviceId
    {
        get => _deviceId;
        set
        {
            if (_deviceId == value)
            {
                return;
            }

            _deviceId = value;
            bool running;
            lock (_gate)
            {
                running = _thread is not null;
            }

            if (running)
            {
                Failed?.Invoke(this, null);
            }
        }
    }

    public AudioOutputFormat Start(AudioRenderCallback render)
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                throw new InvalidOperationException("Already started.");
            }

            var device = _deviceId is null
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : _enumerator.GetDevice(_deviceId);

            var client = device.AudioClient;
            var format = client.MixFormat;
            var ready = new ManualResetEventSlim(false);
            Exception? startError = null;
            var stop = new ManualResetEvent(false);
            _stop = stop;

            _thread = new Thread(() => RenderLoop(device, client, format, render, stop, ready, ex => startError = ex))
            {
                Name = "MaSatellite.Audio",
                IsBackground = true,
                Priority = ThreadPriority.Highest,
            };
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            if (startError is not null)
            {
                _thread.Join(1000);
                _thread = null;
                stop.Dispose();
                _stop = null;
                throw new InvalidOperationException("Could not start audio output: " + startError.Message, startError);
            }

            if (!_registered)
            {
                _enumerator.RegisterEndpointNotificationCallback(this);
                _registered = true;
            }

            _logger.LogInformation("WASAPI output on '{Device}': {Rate} Hz, {Channels} ch, {Bits} bit", device.FriendlyName, format.SampleRate, format.Channels, format.BitsPerSample);
            return new AudioOutputFormat(format.SampleRate, format.Channels);
        }
    }

    public void Stop()
    {
        Thread? thread;
        ManualResetEvent? stop;
        lock (_gate)
        {
            thread = _thread;
            stop = _stop;
            _thread = null;
            _stop = null;
        }

        if (thread is null)
        {
            return;
        }

        stop?.Set();
        thread.Join(2000);
        stop?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        if (_registered)
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
            _registered = false;
        }

        _enumerator.Dispose();
    }

    private void RenderLoop(MMDevice device, AudioClient client, WaveFormat format, AudioRenderCallback render, ManualResetEvent stop, ManualResetEventSlim ready, Action<Exception> reportStartError)
    {
        var avrtHandle = AvSetMmThreadCharacteristics("Pro Audio", out _);
        using var bufferReady = new AutoResetEvent(false);
        var started = false;
        try
        {
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, BufferDurationHns, 0, format, Guid.Empty);
            client.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle());
            var renderClient = client.AudioRenderClient;
            var bufferFrames = client.BufferSize;
            var channels = format.Channels;
            var rate = format.SampleRate;
            var targetFrames = Math.Min(bufferFrames, rate * TargetBufferMs / 1000);
            var streamLatencyMicros = client.StreamLatency / 10; // 100 ns units

            // Prime with silence-or-audio so the device does not start empty.
            WriteBuffer(renderClient, render, channels, targetFrames, _clock.NowMicros + streamLatencyMicros);
            client.Start();
            started = true;
            ready.Set();

            var handles = new WaitHandle[] { bufferReady, stop };
            while (true)
            {
                var signalled = WaitHandle.WaitAny(handles, 2000);
                if (signalled == 1)
                {
                    break;
                }

                var padding = client.CurrentPadding;
                var frames = Math.Min(bufferFrames - padding, targetFrames - padding);
                if (frames <= 0)
                {
                    continue;
                }

                var playout = _clock.NowMicros + streamLatencyMicros + (padding * 1_000_000L / rate);
                WriteBuffer(renderClient, render, channels, frames, playout);
            }
        }
        catch (Exception ex)
        {
            if (!started)
            {
                reportStartError(ex);
                ready.Set();
            }
            else
            {
                _logger.LogWarning(ex, "WASAPI render loop failed");
                Failed?.Invoke(this, ex);
            }
        }
        finally
        {
            try
            {
                if (started)
                {
                    client.Stop();
                    client.Reset();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stopping the WASAPI client failed");
            }

            client.Dispose();
            device.Dispose();
            if (avrtHandle != IntPtr.Zero)
            {
                AvRevertMmThreadCharacteristics(avrtHandle);
            }
        }
    }

    private static unsafe void WriteBuffer(AudioRenderClient renderClient, AudioRenderCallback render, int channels, int frames, long playoutMicros)
    {
        var ptr = renderClient.GetBuffer(frames);
        var span = new Span<float>((void*)ptr, frames * channels);
        render(span, playoutMicros);
        renderClient.ReleaseBuffer(frames, AudioClientBufferFlags.None);
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia && _deviceId is null && _thread is not null)
        {
            Failed?.Invoke(this, null);
        }
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (deviceId == _deviceId && newState != DeviceState.Active && _thread is not null)
        {
            Failed?.Invoke(this, null);
        }
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
    {
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
        if (deviceId == _deviceId && _thread is not null)
        {
            Failed?.Invoke(this, null);
        }
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

    [DllImport("avrt.dll", EntryPoint = "AvRevertMmThreadCharacteristics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
