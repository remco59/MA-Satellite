using System.Text.Json.Serialization;
using MaSatellite.Core.Session;

namespace MaSatellite.Host.Settings;

public enum ConnectionMode
{
    /// <summary>Advertise via mDNS and wait for the server to connect (recommended by the spec).</summary>
    Advertise,

    /// <summary>Connect out to <see cref="AppSettings.MaHost"/>.</summary>
    Connect,
}

/// <summary>User-editable settings persisted as JSON.</summary>
public sealed record AppSettings
{
    public const int DefaultListenPort = 8928;
    public const int DefaultServerPort = 8927;
    public const int DefaultMaWebPort = 8095;
    public const int DefaultLocalApiPort = 8929;

    /// <summary>Stable id sent to the server so it remembers groups and settings.</summary>
    public string ClientId { get; init; } = Guid.NewGuid().ToString("D");

    public string PlayerName { get; init; } = Environment.MachineName;

    /// <summary>MMDevice id of the output device; null means the system default.</summary>
    public string? OutputDeviceId { get; init; }

    /// <summary>Music Assistant host (name or IP), e.g. 192.168.0.98.</summary>
    public string MaHost { get; init; } = "";

    public int MaWebPort { get; init; } = DefaultMaWebPort;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConnectionMode ConnectionMode { get; init; } = ConnectionMode.Advertise;

    public int ListenPort { get; init; } = DefaultListenPort;

    /// <summary>Port of the server's Sendspin endpoint when <see cref="ConnectionMode.Connect"/> is used.</summary>
    public int ServerPort { get; init; } = DefaultServerPort;

    public bool Autostart { get; init; } = true;

    /// <summary>Extra delay (0-5000 ms) compensating for output hardware after the audio port.</summary>
    public int StaticDelayMs { get; init; }

    /// <summary>Auto speaks the encrypted protocol and falls back to the legacy plaintext protocol for older servers.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SendspinProtocolMode ProtocolMode { get; init; } = SendspinProtocolMode.Auto;

    /// <summary>Encrypted protocol: let a server that is not paired with this player start playback (anyone on the network who can reach the server's operator interface could be the server).</summary>
    public bool AllowUnpairedAccess { get; init; } = true;

    public bool LocalApiEnabled { get; init; } = true;

    public int LocalApiPort { get; init; } = DefaultLocalApiPort;
}
