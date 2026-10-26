using MaSatellite.Core.Time;

namespace MaSatellite.Core.Tests;

public class TimeFilterTests
{
    private const long TrueOffset = 5_000_000_000; // server clock = client clock + 5000 s

    private static (long t1, long t2, long t3, long t4) Exchange(long clientNow, long upDelay, long downDelay, long serverProc = 50, double driftPpm = 0)
    {
        // server clock = client clock + offset(t), with offset growing by driftPpm per second of client time
        long Offset(long t) => TrueOffset + (long)(driftPpm * 1e-6 * t);
        var t1 = clientNow;
        var t2 = t1 + upDelay + Offset(t1 + upDelay);
        var t3 = t2 + serverProc;
        var t4 = t1 + upDelay + serverProc + downDelay;
        return (t1, t2, t3, t4);
    }

    [Fact]
    public void First_sample_sets_offset_directly()
    {
        var f = new TimeFilter();
        var (t1, t2, t3, t4) = Exchange(1_000_000, 500, 500);
        Assert.True(f.Update(t1, t2, t3, t4));
        Assert.Equal(1, f.Count);
        Assert.InRange(f.OffsetMicros, TrueOffset - 10, TrueOffset + 10);
    }

    [Fact]
    public void Discards_non_increasing_time_and_non_positive_error()
    {
        var f = new TimeFilter();
        Assert.True(f.Update(100, 10, 2000));
        Assert.False(f.Update(100, 10, 2000)); // same time_added
        Assert.False(f.Update(100, 0, 3000)); // max_error 0
        Assert.False(f.Update(100, -5, 3000));
        Assert.Equal(1, f.Count);
    }

    [Fact]
    public void Converges_with_jittery_symmetric_network()
    {
        var f = new TimeFilter();
        var rng = new Random(7);
        long now = 1_000_000;
        for (var i = 0; i < 200; i++)
        {
            var up = 400 + rng.Next(0, 600);
            var down = 400 + rng.Next(0, 600);
            var (t1, t2, t3, t4) = Exchange(now, up, down);
            f.Update(t1, t2, t3, t4);
            now += 250_000;
        }

        Assert.InRange(f.OffsetMicros, TrueOffset - 400, TrueOffset + 400);
        Assert.True(f.StdDevMicros < 500, $"stddev {f.StdDevMicros}");
    }

    [Fact]
    public void Tracks_drift()
    {
        var f = new TimeFilter();
        var rng = new Random(3);
        long now = 1_000_000;
        const double ppm = 50; // 50 µs of offset change per second
        for (var i = 0; i < 400; i++)
        {
            var (t1, t2, t3, t4) = Exchange(now, 300 + rng.Next(0, 100), 300 + rng.Next(0, 100), driftPpm: ppm);
            f.Update(t1, t2, t3, t4);
            now += 250_000;
        }

        Assert.InRange(f.DriftPerMicro, ppm * 1e-6 * 0.7, ppm * 1e-6 * 1.3);
        // Conversion round trip stays accurate when drift is applied.
        var server = f.ComputeServerTime(now);
        var truth = now + TrueOffset + (long)(ppm * 1e-6 * now);
        Assert.InRange(Math.Abs(server - truth), 0, 300);
    }

    [Fact]
    public void Conversions_are_inverse_of_each_other()
    {
        var f = new TimeFilter();
        for (var i = 0; i < 20; i++)
        {
            var (t1, t2, t3, t4) = Exchange(1_000_000 + (i * 200_000), 500, 500);
            f.Update(t1, t2, t3, t4);
        }

        const long clientTime = 6_000_000;
        var back = f.ComputeClientTime(f.ComputeServerTime(clientTime));
        Assert.InRange(back, clientTime - 1, clientTime + 1);
    }

    [Fact]
    public void Large_residual_after_min_samples_adapts_quickly()
    {
        var f = new TimeFilter();
        long now = 1_000_000;
        for (var i = 0; i < 150; i++)
        {
            var (t1, t2, t3, t4) = Exchange(now, 500, 500);
            f.Update(t1, t2, t3, t4);
            now += 250_000;
        }

        // The server clock steps by 20 ms; with forgetting the filter must re-converge within a few samples.
        for (var i = 0; i < 12; i++)
        {
            var (t1, t2, t3, t4) = Exchange(now, 500, 500);
            f.Update(t1, t2 + 20_000, t3 + 20_000, t4);
            now += 250_000;
        }

        Assert.InRange(f.OffsetMicros, TrueOffset + 20_000 - 2_000, TrueOffset + 20_000 + 2_000);
    }
}
