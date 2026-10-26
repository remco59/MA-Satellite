using System.Diagnostics;
using MaSatellite.Core.Audio;
using MaSatellite.Core.Time;

namespace MaSatellite.Core.Tests.Support;

/// <summary>A device that consumes audio in real time on an exact sample grid and records what it was given.</summary>
internal sealed class FakeAudioOutput : IAudioOutput
{
    private readonly ILocalClock _clock;
    private readonly long _latencyMicros;
    private readonly List<(long PlayoutMicros, float[] Samples)> _captured = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public FakeAudioOutput(ILocalClock clock, long latencyMicros = 30_000)
    {
        _clock = clock;
        _latencyMicros = latencyMicros;
    }

    public event EventHandler<Exception?>? Failed;

    public int Rate { get; } = 48000;

    public int Channels { get; } = 2;

    public int Starts { get; private set; }

    public AudioOutputFormat Start(AudioRenderCallback render)
    {
        Starts++;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var startLocal = _clock.NowMicros;
        _loop = Task.Run(async () =>
        {
            const int frames = 480;
            long rendered = 0;
            var sw = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                // stay ~100 ms ahead of the device clock, like a WASAPI buffer
                while (!ct.IsCancellationRequested && (rendered * 1000.0 / Rate) < sw.Elapsed.TotalMilliseconds + 100)
                {
                    var buf = new float[frames * Channels];
                    var playout = startLocal + _latencyMicros + (rendered * 1_000_000L / Rate);
                    render(buf, playout);
                    lock (_captured)
                    {
                        _captured.Add((playout, buf));
                    }

                    rendered += frames;
                }

                try
                {
                    await Task.Delay(5, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
        return new AudioOutputFormat(Rate, Channels);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(1000);
        }
        catch (AggregateException)
        {
        }
    }

    public void Dispose() => Stop();

    public void RaiseFailed() => Failed?.Invoke(this, new IOException("device unplugged"));

    /// <summary>Local time (µs) at which the first non-silent sample is audible, or null.</summary>
    public long? FirstSoundMicros()
    {
        lock (_captured)
        {
            foreach (var (playout, samples) in _captured)
            {
                for (var i = 0; i < samples.Length; i += Channels)
                {
                    if (Math.Abs(samples[i]) > 1e-4f)
                    {
                        return playout + (i / Channels * 1_000_000L / Rate);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Left-channel samples from the first sound onward.</summary>
    public float[] LeftFromFirstSound(int maxFrames)
    {
        lock (_captured)
        {
            var all = new List<float>();
            var started = false;
            foreach (var (_, samples) in _captured)
            {
                for (var i = 0; i < samples.Length; i += Channels)
                {
                    if (!started && Math.Abs(samples[i]) > 1e-4f)
                    {
                        started = true;
                    }

                    if (started)
                    {
                        all.Add(samples[i]);
                        if (all.Count >= maxFrames)
                        {
                            return [.. all];
                        }
                    }
                }
            }

            return [.. all];
        }
    }
}
