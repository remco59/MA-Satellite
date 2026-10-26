namespace MaSatellite.Core.Protocol;

public sealed record ServerHello(string ServerId, string Name, int Version, IReadOnlyList<string> ActiveRoles, string? ConnectionReason);

public sealed record ServerTime(long ClientTransmitted, long ServerReceived, long ServerTransmitted);

public sealed record PlayerCommand(string Command, int? Volume, bool? Mute, int? StaticDelayMs);

public sealed record ServerCommand(PlayerCommand? Player);

public sealed record StreamPlayerFormat(string Codec, int SampleRate, int Channels, int BitDepth, string? CodecHeader);

public sealed record StreamArtworkChannel(string Source, string Format, int Width, int Height);

public sealed record StreamStart(StreamPlayerFormat? Player, IReadOnlyList<StreamArtworkChannel>? ArtworkChannels);

public sealed record StreamClear(IReadOnlyList<string>? Roles);

public sealed record StreamEnd(IReadOnlyList<string>? Roles);

public static class ConnectionReasons
{
    public const string Discovery = "discovery";
    public const string Playback = "playback";
}
