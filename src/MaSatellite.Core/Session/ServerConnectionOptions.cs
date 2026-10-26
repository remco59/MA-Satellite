namespace MaSatellite.Core.Session;

public enum ConnectionMode
{
    /// <summary>Advertise via mDNS and wait for the server (spec-recommended).</summary>
    Advertise,

    /// <summary>Open the WebSocket to a configured server ourselves.</summary>
    Connect,
}

public sealed record ServerConnectionOptions
{
    public ConnectionMode Mode { get; init; } = ConnectionMode.Advertise;

    public int ListenPort { get; init; } = 8928;

    public string Path { get; init; } = "/sendspin";

    public string ServerHost { get; init; } = string.Empty;

    public int ServerPort { get; init; } = 8927;
}

/// <summary>Remembers which server played last, so two servers that both ask for "discovery" can be arbitrated.</summary>
public interface ILastServerStore
{
    string? LastPlayedServerId { get; set; }
}

public sealed class InMemoryLastServerStore : ILastServerStore
{
    public string? LastPlayedServerId { get; set; }
}
