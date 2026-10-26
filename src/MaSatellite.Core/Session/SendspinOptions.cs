using MaSatellite.Core.Protocol;

namespace MaSatellite.Core.Session;

/// <summary>Which Sendspin protocol to speak to a server.</summary>
public enum SendspinProtocolMode
{
    /// <summary>Encrypted first; fall back to the legacy plaintext protocol for servers that do not answer the encrypted handshake.</summary>
    Auto,

    /// <summary>Only the encrypted protocol (Noise, optional pairing).</summary>
    Encrypted,

    /// <summary>Only the legacy plaintext protocol.</summary>
    Legacy,
}

public sealed record SendspinOptions
{
    public SendspinProtocolMode ProtocolMode { get; init; } = SendspinProtocolMode.Auto;

    /// <summary>Encrypted protocol: let an operator-approved but unpaired server activate this player (man-in-the-middle possible).</summary>
    public bool AllowUnpairedAccess { get; init; } = true;

    /// <summary>How long the encrypted handshake may take before we fall back (Auto) or give up.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How long a connection may stay without <c>server/activate</c> before it is dropped.</summary>
    public TimeSpan ActivationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public string ClientId { get; init; } = Guid.NewGuid().ToString("D");

    public string PlayerName { get; init; } = "ma-satellite";

    public string SoftwareVersion { get; init; } = "0.1.0";

    /// <summary>Extra delay after the audio port (external amplifiers etc.), 0-5000 ms.</summary>
    public int StaticDelayMs { get; init; }

    /// <summary>Lead time the server must give us before the first chunk plays (device start, decode warm-up).</summary>
    public int RequiredLeadTimeMs { get; init; } = 250;

    /// <summary>Jitter buffer the server should keep ahead of playback. Generous by default; tune after measuring.</summary>
    public int MinBufferMs { get; init; } = 1000;

    /// <summary>Max bytes of compressed audio the server may have queued with us.</summary>
    public int BufferCapacityBytes { get; init; } = 4_000_000;

    public bool RequestArtwork { get; init; } = true;

    public int ArtworkSize { get; init; } = 512;

    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan TimeBurstInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long we may be out of sync before reporting <c>state: error</c> to the server.</summary>
    public TimeSpan SyncErrorDebounce { get; init; } = TimeSpan.FromSeconds(1.5);

    public IReadOnlyList<AudioFormatSpec> SupportedFormats { get; init; } = DefaultFormats;

    public static IReadOnlyList<AudioFormatSpec> DefaultFormats { get; } = BuildDefaultFormats();

    private static List<AudioFormatSpec> BuildDefaultFormats()
    {
        var list = new List<AudioFormatSpec>();
        foreach (var rate in new[] { 48000, 44100, 96000, 88200 })
        {
            list.Add(new AudioFormatSpec("flac", 2, rate, 16));
        }

        foreach (var rate in new[] { 48000, 44100, 96000, 88200 })
        {
            list.Add(new AudioFormatSpec("flac", 2, rate, 24));
        }

        foreach (var rate in new[] { 48000, 44100 })
        {
            list.Add(new AudioFormatSpec("pcm", 2, rate, 16));
        }

        return list;
    }
}
