using System.Threading;

namespace MaSatellite.Host.Infrastructure;

/// <summary>Holds a named mutex so only one copy of the app runs per user session.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private bool _owned;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activationEvent, bool owned)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _owned = owned;
    }

    public bool IsPrimary => _owned;

    public static SingleInstanceGuard Acquire(string name)
    {
        // Open the activation event before competing for the mutex. This ensures that,
        // whichever process wins the mutex, every concurrently-starting process already
        // holds a handle to the same event and cannot lose an early activation signal.
        var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            name: name + ".Activate");
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        return new SingleInstanceGuard(mutex, activationEvent, createdNew);
    }

    /// <summary>Signals the primary process to bring its full window to the foreground.</summary>
    public void SignalActivation()
    {
        if (_owned)
        {
            throw new InvalidOperationException("The primary instance cannot signal itself.");
        }

        _activationEvent.Set();
    }

    /// <summary>Waits for another launch request. Call only from a background thread on the primary instance.</summary>
    public bool WaitForActivation(CancellationToken cancellationToken)
    {
        if (!_owned)
        {
            throw new InvalidOperationException("Only the primary instance can wait for activation requests.");
        }

        return WaitHandle.WaitAny([_activationEvent, cancellationToken.WaitHandle]) == 0;
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex.ReleaseMutex();
            _owned = false;
        }

        _activationEvent.Dispose();
        _mutex.Dispose();
    }
}
