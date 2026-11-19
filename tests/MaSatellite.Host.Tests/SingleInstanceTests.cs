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
    public async Task Second_instance_signals_primary_to_activate()
    {
        var name = "MaSatellite.ActivationTest." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var primaryReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primaryResult = new TaskCompletionSource<(bool IsPrimary, bool Activated)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primaryThread = new Thread(() =>
        {
            try
            {
                // A mutex must be released by the same thread that acquired it.
                using var first = SingleInstanceGuard.Acquire(name);
                primaryReady.TrySetResult(first.IsPrimary);
                var activated = first.WaitForActivation(timeout.Token);
                primaryResult.TrySetResult((first.IsPrimary, activated));
            }
            catch (Exception ex)
            {
                primaryReady.TrySetException(ex);
                primaryResult.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
        };
        primaryThread.Start();

        Assert.True(await primaryReady.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var secondaryResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondaryThread = new Thread(() =>
        {
            try
            {
                using var second = SingleInstanceGuard.Acquire(name);
                var isPrimary = second.IsPrimary;
                second.SignalActivation();
                secondaryResult.TrySetResult(isPrimary);
            }
            catch (Exception ex)
            {
                secondaryResult.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
        };
        secondaryThread.Start();

        Assert.False(await secondaryResult.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var primary = await primaryResult.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(primary.IsPrimary);
        Assert.True(primary.Activated);
    }
}
