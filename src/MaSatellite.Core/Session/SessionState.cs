using MaSatellite.Core.Protocol;

namespace MaSatellite.Core.Session;

public sealed record ArtworkImage(byte[] Data, string Format, int Version);

/// <summary>Everything the server told us about the current connection, as an immutable snapshot.</summary>
public sealed record SessionState
{
    public string? ServerId { get; init; }

    public string? ServerName { get; init; }

    public string? ConnectionReason { get; init; }

    public MetadataState Metadata { get; init; } = new();

    public ControllerState Controller { get; init; } = new();

    public GroupState Group { get; init; } = new();

    public ArtworkImage? Artwork { get; init; }

    public StreamPlayerFormat? Stream { get; init; }

    public bool StreamActive => Stream is not null;
}
