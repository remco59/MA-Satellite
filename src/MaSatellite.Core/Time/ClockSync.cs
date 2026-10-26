using MaSatellite.Core.Protocol;

namespace MaSatellite.Core.Time;

/// <summary>
/// Maintains the server↔local clock mapping: sends bursts of <c>client/time</c> messages (each after the previous
/// reply), keeps the exchange with the smallest error per burst and feeds it to the <see cref="TimeFilter"/>.
/// </summary>
public sealed class ClockSync : IClockMapper
{
    public const int BurstSize = 8;
    public const int ConvergedUpdates = 3;

    private readonly ILocalClock _clock;
    private readonly object _gate = new();
    private TimeFilter _filter = new();
    private TaskCompletionSource<(ServerTime Time, long ReceivedAt)>? _pending;
    private long _pendingSentAt;

    public ClockSync(ILocalClock clock)
    {
        _clock = clock;
    }

    public bool IsSynchronized
    {
        get
        {
            lock (_gate)
            {
                return _filter.Count >= ConvergedUpdates;
            }
        }
    }

    public double StdDevMicros
    {
        get
        {
            lock (_gate)
            {
                return _filter.StdDevMicros;
            }
        }
    }

    public long ServerToLocal(long serverMicros)
    {
        lock (_gate)
        {
            return _filter.ComputeClientTime(serverMicros);
        }
    }

    public long LocalToServer(long localMicros)
    {
        lock (_gate)
        {
            return _filter.ComputeServerTime(localMicros);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _filter = new TimeFilter();
        }
    }

    /// <summary>Called from the receive loop for every <c>server/time</c>; <paramref name="receivedAtMicros"/> is T4.</summary>
    public void OnServerTime(ServerTime time, long receivedAtMicros)
    {
        TaskCompletionSource<(ServerTime Time, long ReceivedAt)>? tcs;
        lock (_gate)
        {
            tcs = _pending;
            if (tcs is null || time.ClientTransmitted != _pendingSentAt)
            {
                return;
            }

            _pending = null;
        }

        tcs.TrySetResult((time, receivedAtMicros));
    }

    /// <summary>Runs until cancelled. <paramref name="sendTimeMessage"/> sends the JSON for a client/time message.</summary>
    public async Task RunAsync(Func<string, Task> sendTimeMessage, TimeSpan burstInterval, CancellationToken cancellationToken)
    {
        var burst = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            long bestError = long.MaxValue;
            (long T1, long T2, long T3, long T4) best = default;
            for (var i = 0; i < BurstSize; i++)
            {
                var t1 = _clock.NowMicros;
                var tcs = new TaskCompletionSource<(ServerTime Time, long ReceivedAt)>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    _pending = tcs;
                    _pendingSentAt = t1;
                }

                await sendTimeMessage(MessageCodec.ClientTime(t1)).ConfigureAwait(false);

                (ServerTime Time, long ReceivedAt) reply;
                try
                {
                    reply = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                var t4 = reply.ReceivedAt;
                var error = (((t4 - t1) - (reply.Time.ServerTransmitted - reply.Time.ServerReceived)) / 2);
                if (error > 0 && error < bestError)
                {
                    bestError = error;
                    best = (t1, reply.Time.ServerReceived, reply.Time.ServerTransmitted, t4);
                }
            }

            if (bestError != long.MaxValue)
            {
                lock (_gate)
                {
                    _filter.Update(best.T1, best.T2, best.T3, best.T4);
                }
            }

            burst++;
            // Converge quickly at the start, then keep bursts well inside the 10 s the spec allows.
            var delay = burst <= ConvergedUpdates ? TimeSpan.FromMilliseconds(100) : burstInterval;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
