using System.Diagnostics;

namespace MaSatellite.Core.Time;

/// <summary>Monotonic local clock in microseconds. Its epoch is arbitrary; only differences matter.</summary>
public interface ILocalClock
{
    long NowMicros { get; }
}

public sealed class StopwatchClock : ILocalClock
{
    public static readonly StopwatchClock Instance = new();

    public long NowMicros => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));
}

/// <summary>Maps between the server's clock and the local clock.</summary>
public interface IClockMapper
{
    bool IsSynchronized { get; }

    long ServerToLocal(long serverMicros);

    long LocalToServer(long localMicros);
}
