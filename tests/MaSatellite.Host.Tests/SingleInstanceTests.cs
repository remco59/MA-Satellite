using MaSatellite.Host.Infrastructure;

namespace MaSatellite.Host.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void Second_acquire_is_not_primary_until_first_released()
    {
        var name = "MaSatellite.Test." + Guid.NewGuid().ToString("N");
        // Mutexes are thread-affine: ownership checks must happen on one thread each.
        bool secondPrimary = true;
        using (var first = SingleInstanceGuard.Acquire(name))
        {
            Assert.True(first.IsPrimary);
            var t = new Thread(() =>
            {
                using var second = SingleInstanceGuard.Acquire(name);
                secondPrimary = second.IsPrimary;
            });
            t.Start();
            t.Join();
        }

        Assert.False(secondPrimary);
    }
    [Fact]
    public void Second_instance_signals_primary_to_activate()
    {
        var name = "MaSatellite.ActivationTest." + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.Acquire(name);
        Assert.True(first.IsPrimary);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var activation = Task.Run(() => first.WaitForActivation(timeout.Token));
        var secondPrimary = true;
        var second = new Thread(() =>
        {
            using var instance = SingleInstanceGuard.Acquire(name);
            secondPrimary = instance.IsPrimary;
            instance.SignalActivation();
        });

        second.Start();
        Assert.True(second.Join(TimeSpan.FromSeconds(5)));
        Assert.False(secondPrimary);
        Assert.True(activation.GetAwaiter().GetResult());
    }

}
