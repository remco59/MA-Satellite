using MaSatellite.Core.Protocol;

namespace MaSatellite.Core.Session;

public sealed record ArtworkImage(byte[] Data, string Format, int Version);

/// <summary>How the connection is protected.</summary>
public enum SessionSecurity
{
    /// <summary>Legacy plaintext protocol: anyone on the network can read it.</summary>
    Plaintext,

    /// <summary>Encrypted, but the server is not paired (man-in-the-middle is possible).</summary>
    EncryptedUnpaired,

    /// <summary>Encrypted and authenticated with a long-term pairing PSK.</summary>
    EncryptedPaired,
}

/// <summary>Everything the server told us about the current connection, as an immutable snapshot.</summary>
public sealed record SessionState
{
    public string? ServerId { get; init; }

    public string? ServerName { get; init; }

    public string? ConnectionReason { get; init; }

    public SessionSecurity Security { get; init; }

    /// <summary>Encrypted protocol: roles the server activated (empty until the operator approves or pairs this player).</summary>
    public IReadOnlyList<string> ActiveRoles { get; init; } = Array.Empty<string>();

    /// <summary>Encrypted protocol: the server's current purposes on this connection (<c>playback</c>, <c>pairing</c>).</summary>
    public IReadOnlyList<string> Activities { get; init; } = Array.Empty<string>();

    /// <summary>Encrypted protocol: a pairing attempt is running on this connection.</summary>
    public bool Pairing { get; init; }

    /// <summary>True when connected over the encrypted protocol but nothing is activated yet (waiting for approval or pairing in the server).</summary>
    public bool AwaitingApproval { get; init; }

    public MetadataState Metadata { get; init; } = new();

    public ControllerState Controller { get; init; } = new();

    public GroupState Group { get; init; } = new();

    public ArtworkImage? Artwork { get; init; }

    public StreamPlayerFormat? Stream { get; init; }

    public bool StreamActive => Stream is not null;
}
