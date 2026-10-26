namespace MaSatellite.Core.Session;

public enum ConnectionState
{
    Stopped,

    /// <summary>Advertising and waiting for a server to connect.</summary>
    Listening,

    /// <summary>Trying to reach the configured server.</summary>
    Connecting,
    Connected,
}

public sealed record SatelliteStatus
{
    public ConnectionState Connection { get; init; }

    public string? Detail { get; init; }

    public SessionState Session { get; init; } = new();

    public int Volume { get; init; }

    public bool Muted { get; init; }

    public bool InSync { get; init; }

    public string? ServerName => Session.ServerName;
}
