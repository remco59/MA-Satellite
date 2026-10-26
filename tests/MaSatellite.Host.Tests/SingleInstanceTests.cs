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
}
