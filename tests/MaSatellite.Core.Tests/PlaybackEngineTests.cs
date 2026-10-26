using MaSatellite.Core.Audio;
using MaSatellite.Core.Time;

namespace MaSatellite.Core.Tests;

public class PlaybackEngineTests
{
    private sealed class LinearClock : IClockMapper
    {
        public long Offset { get; set; } = 1_000_000_000;

        /// <summary>Server seconds per local second minus one (e.g. 100e-6 = server clock runs 100 ppm fast).</summary>
        public double Skew { get; set; }

        public bool IsSynchronized { get; set; } = true;

        public long ServerToLocal(long serverMicros) => (long)((serverMicros - Offset) / (1 + Skew));

        public long LocalToServer(long localMicros) => Offset + (long)(localMicros * (1 + Skew));
    }

    private const int Rate = 48000;

    private static float[] Ramp(long firstFrame, int frames, int channels = 2)
    {
        var data = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                data[(i * channels) + c] = (firstFrame + i + 1) * 1e-6f;
            }
        }

        return data;
    }

    private static long TsFor(long frame, long originServerMicros, int rate = Rate) => originServerMicros + (frame * 1_000_000L / rate);

    private static PlaybackEngine NewEngine(LinearClock clock, int srcRate = Rate, int srcCh = 2, int outRate = Rate, int outCh = 2)
    {
        var e = new PlaybackEngine(clock);
        e.Configure(new AudioStreamFormat(srcRate, srcCh, 16), outRate, outCh);
        return e;
    }

    /// <summary>Plays <paramref name="seconds"/> of output in 10 ms device buffers, returning all frames.</summary>
    private static float[] RunDevice(PlaybackEngine engine, double seconds, int bufFrames = 480, long startLocal = 0, int outRate = Rate, int outCh = 2, Action<long>? beforeFill = null)
    {
        var total = (int)(seconds * outRate / bufFrames) * bufFrames;
        var result = new float[total * outCh];
        var buf = new float[bufFrames * outCh];
        long local = startLocal;
        for (var done = 0; done < total; done += bufFrames)
        {
            beforeFill?.Invoke(local);
            engine.Fill(buf, local);
            buf.CopyTo(result, done * outCh);
            local += bufFrames * 1_000_000L / outRate;
        }

        return result;
    }

    [Fact]
    public void Plays_nothing_before_start_timestamp_and_starts_on_the_frame()
    {
        var clock = new LinearClock();
        var engine = NewEngine(clock);
        var origin = clock.Offset + 500_000; // 500 ms after local zero
        for (var f = 0; f < 48000; f += 480)
        {
            engine.Enqueue(TsFor(f, origin), Ramp(f, 480));
        }

        var output = RunDevice(engine, 1.0);

        const int expectedStart = 24000;
        var firstNonZero = Array.FindIndex(output, v => v != 0f) / 2;
        Assert.InRange(firstNonZero, expectedStart - 2, expectedStart + 2);
        Assert.InRange(output[firstNonZero * 2] * 1e6, 0.5, 3.5); // the very first source frame, not something later
        // contiguous ramp from there on: each frame advances by exactly one source frame
        for (var i = firstNonZero + 1; i < firstNonZero + 2000; i++)
        {
            Assert.Equal(1e-6, output[(i * 2)] - output[((i - 1) * 2)], 1e-9);
        }
    }

    [Fact]
    public void Static_delay_makes_playback_start_earlier()
    {
        var clock = new LinearClock();
        var engine = NewEngine(clock);
        engine.StaticDelayMs = 100;
        var origin = clock.Offset + 500_000;
        for (var f = 0; f < 48000; f += 480)
        {
            engine.Enqueue(TsFor(f, origin), Ramp(f, 480));
        }

        var output = RunDevice(engine, 1.0);
        var firstNonZero = Array.FindIndex(output, v => v != 0f) / 2;
        Assert.InRange(firstNonZero, 19200 - 2, 19200 + 2); // (500 - 100) ms
    }

    [Fact]
    public void Reports_in_sync_after_start()
    {
        var clock = new LinearClock();
        var engine = NewEngine(clock);
        var events = new List<bool>();
        engine.InSyncChanged += events.Add;
        for (var f = 0; f < 96000; f += 480)
        {
            engine.Enqueue(TsFor(f, clock.Offset + 100_000), Ramp(f, 480));
        }

        RunDevice(engine, 1.0);

        Assert.Equal([true], events);
        Assert.True(engine.Stats.InSync);
        Assert.True(engine.Stats.Playing);
    }

    [Fact]
    public void Late_audio_is_skipped_to_catch_up()
    {
        var clock = new LinearClock();
        var engine = NewEngine(clock);
        // Data starts 200 ms before "now" (local 0 == server offset).
        var origin = clock.Offset - 200_000;
        for (var f = 0; f < 96000; f += 480)
        {
            engine.Enqueue(TsFor(f, origin), Ramp(f, 480));
        }

        var output = RunDevice(engine, 0.2);

        // At local time 0 the sample for server time `offset` is source frame 9600.
        var firstValue = output[0] * 1e6;
        Assert.InRange(firstValue, 9600 - 3, 9600 + 3);
        Assert.InRange(engine.Stats.FramesSkipped, 9500, 9700);
    }

    [Fact]
    public void Timestamp_gap_is_filled_with_silence_and_alignment_is_kept()
    {
        var clock = new LinearClock();
        var engine = NewEngine(clock);
        var origin = clock.Offset + 50_000;
        // 0.5 s of audio, then a 100 ms hole in the timeline, then more audio.
        for (var f = 0; f < 24000; f += 480)
        {
            engine.Enqueue(TsFor(f, origin), Ramp(f, 480));
        }

        for (var f = 24000; f < 48000; f += 480)
        {
            engine.Enqueue(TsFor(f, origin) + 100_000, Ramp(f, 480));
        }

        var output = RunDevice(engine, 1.0);
        var left = output.Where((_, i) => i % 2 == 0).ToArray();

        var first = Array.FindIndex(left, v => v != 0f);
        Assert.InRange(first, 2400 - 2, 2400 + 2);
        var gapStart = first + 24000;
        Assert.Equal(0f, left[gapStart + 100]);
        Assert.Equal(0f, left[gapStart + 4700]);
        // the second part resumes exactly 100 ms (4800 frames) after the first would have ended
        Assert.InRange(left[gapStart + 4800 + 10] * 1e6, 24000 + 10 - 3, 24000 + 10 + 3);
    }

    [Fact]
    public void Resampling_44k1_to_48k_keeps_pitch_and_phase()
    {
        var clock = new LinearClock();
        var engine = NewEngine(clock, srcRate: 44100, srcCh: 1, outRate: 48000, outCh: 2);
        const double freq = 1000;
        var origin = clock.Offset + 100_000;
        const int chunk = 441;
        for (var f = 0; f < 44100; f += chunk)
        {
            var data = new float[chunk];
            for (var i = 0; i < chunk; i++)
            {
                data[i] = (float)(0.5 * Math.Sin(2 * Math.PI * freq * (f + i) / 44100.0));
            }

            engine.Enqueue(TsFor(f, origin, 44100), data);
        }

        var output = RunDevice(engine, 0.8);
        var startFrame = 4800; // 100 ms
        double maxErr = 0;
        for (var j = 200; j < 20000; j++)
        {
            var expected = 0.5 * Math.Sin(2 * Math.PI * freq * (j) / 48000.0);
            maxErr = Math.Max(maxErr, Math.Abs(output[(startFrame + j) * 2] - expected));
        }

        Assert.True(maxErr < 0.01, $"max error {maxErr}");
        Assert.Equal(output[(startFrame + 3000) * 2], output[((startFrame + 3000) * 2) + 1]); // mono duplicated to both channels
    }

    [Fact]
    public void Tracks_clock_skew_without_hard_corrections()
    {
        var clock = new LinearClock { Skew = 100e-6 }; // server clock 100 ppm faster than the DAC clock
        var engine = NewEngine(clock);
        long nextFrame = 0;
        var origin = clock.Offset + 200_000;

        void Feed(long local)
        {
            // keep ~1.5 s buffered, as a real server would
            while (engine.Stats.BufferedMs < 1500 && nextFrame < 48000 * 70)
            {
                engine.Enqueue(TsFor(nextFrame, origin), Ramp(nextFrame, 480));
                nextFrame += 480;
            }
        }

        double worst = 0;
        var buf = new float[960];
        long localTime = 0;
        for (var i = 0; i < 100 * 60; i++) // 60 s in 10 ms buffers
        {
            Feed(localTime);
            engine.Fill(buf, localTime);
            if (i > 3000) // after 30 s the controller must have settled
            {
                worst = Math.Max(worst, Math.Abs(engine.Stats.SyncErrorMicros));
            }

            localTime += 10_000;
        }

        Assert.True(worst < 2000, $"worst steady-state sync error {worst} µs");
        Assert.True(engine.Stats.FramesSkipped < 50, $"skipped {engine.Stats.FramesSkipped}");
        Assert.True(engine.Stats.InSync);
        Assert.Equal(0, engine.Stats.Underruns);
    }

    [Fact]
    public void Underrun_stops_then_resumes_aligned_when_data_returns()
    {
        var clock = new LinearClock();
        var engine = NewEngine(clock);
        var origin = clock.Offset + 50_000;
        for (var f = 0; f < 9600; f += 480) // 200 ms of audio only
        {
            engine.Enqueue(TsFor(f, origin), Ramp(f, 480));
        }

        var events = new List<bool>();
        engine.InSyncChanged += events.Add;
        var output = RunDevice(engine, 0.6);
        Assert.Equal(1, engine.Stats.Underruns);
        Assert.False(engine.Stats.Playing);
        Assert.Contains(false, events);

        // Server catches up: audio for server time 900 ms onward.
        for (var f = 0; f < 4800; f += 480)
        {
            engine.Enqueue(TsFor(f, clock.Offset + 900_000), Ramp(100000 + f, 480));
        }

        var later = RunDevice(engine, 0.5, startLocal: 600_000);
        var first = Array.FindIndex(later, v => v != 0f) / 2;
        Assert.InRange(first, 14400 - 3, 14400 + 3); // 900 ms - 600 ms = 300 ms
        Assert.InRange(later[first * 2] * 1e6, 100001 - 3, 100001 + 3);
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void Clear_discards_buffer()
    {
        var clock = new LinearClock();
        var engine = NewEngine(clock);
        for (var f = 0; f < 48000; f += 480)
        {
            engine.Enqueue(TsFor(f, clock.Offset), Ramp(f, 480));
        }

        engine.Clear();
        var output = RunDevice(engine, 0.1);
        Assert.All(output, v => Assert.Equal(0f, v));
        Assert.Equal(0, engine.Stats.BufferedMs);
    }

    [Fact]
    public void Unsynchronised_clock_outputs_silence()
    {
        var clock = new LinearClock { IsSynchronized = false };
        var engine = NewEngine(clock);
        for (var f = 0; f < 48000; f += 480)
        {
            engine.Enqueue(TsFor(f, clock.Offset), Ramp(f, 480));
        }

        Assert.All(RunDevice(engine, 0.2), v => Assert.Equal(0f, v));
    }
}
