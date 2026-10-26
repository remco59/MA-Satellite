using System.Text.Json.Serialization;

namespace MaSatellite.Core.Protocol;

/// <summary>Roles this client can advertise. Names are the wire values.</summary>
public static class Roles
{
    public const string Player = "player@v1";
    public const string Controller = "controller@v1";
    public const string Metadata = "metadata@v1";
    public const string Artwork = "artwork@v1";
}

public sealed record DeviceInfo(string? ProductName, string? Manufacturer, string? SoftwareVersion);

public sealed record AudioFormatSpec(string Codec, int Channels, int SampleRate, int BitDepth);

public sealed record PlayerSupport(
    IReadOnlyList<AudioFormatSpec> SupportedFormats,
    int BufferCapacity,
    IReadOnlyList<string> SupportedCommands);

public sealed record ArtworkChannelSpec(string Source, string Format, int MediaWidth, int MediaHeight);

public sealed record ArtworkSupport(IReadOnlyList<ArtworkChannelSpec> Channels);

public sealed record ClientHello(
    string ClientId,
    string Name,
    DeviceInfo? DeviceInfo,
    int Version,
    IReadOnlyList<string> SupportedRoles,
    [property: JsonPropertyName("player@v1_support")] PlayerSupport? PlayerSupport,
    [property: JsonPropertyName("artwork@v1_support")] ArtworkSupport? ArtworkSupport);

public sealed record ClientTime(long ClientTransmitted);

public static class ClientStates
{
    public const string Synchronized = "synchronized";
    public const string Error = "error";
    public const string ExternalSource = "external_source";
}

public sealed record PlayerStatePayload(
    int? Volume,
    bool? Muted,
    int StaticDelayMs,
    int RequiredLeadTimeMs,
    int MinBufferMs,
    IReadOnlyList<string>? SupportedCommands);

public sealed record ClientState(string State, PlayerStatePayload? Player);

public sealed record ControllerCommand(string Command, int? Volume = null, bool? Mute = null, long? PositionMs = null, long? OffsetMs = null);

public sealed record ClientCommand(ControllerCommand Controller);

public sealed record ClientGoodbye(string Reason);

public static class GoodbyeReasons
{
    public const string AnotherServer = "another_server";
    public const string Shutdown = "shutdown";
    public const string Restart = "restart";
    public const string UserRequest = "user_request";
}

public sealed record PlayerFormatRequest(string? Codec = null, int? Channels = null, int? SampleRate = null, int? BitDepth = null);

public sealed record ArtworkFormatRequest(int Channel, string? Source = null, string? Format = null, int? MediaWidth = null, int? MediaHeight = null);

public sealed record StreamRequestFormat(PlayerFormatRequest? Player = null, ArtworkFormatRequest? Artwork = null);
