using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaSatellite.Core.Protocol;

public sealed record UnpairedAccess(bool Enabled);

public sealed record PairMethodDescriptor(IReadOnlyList<string>? Locations);

/// <summary>Encrypted-protocol <c>client/hello</c>. Identity and version travel in <c>client/init</c> instead.</summary>
public sealed record ClientHelloV2(
    string Name,
    DeviceInfo? DeviceInfo,
    IReadOnlyList<string> SupportedRoles,
    [property: JsonPropertyName("player@v1_support")] PlayerSupport? PlayerSupport,
    IReadOnlyDictionary<string, PairMethodDescriptor> SupportedPairMethods,
    UnpairedAccess UnpairedAccess);

public sealed record PlayerStateV2(
    int? Volume,
    bool? Muted,
    int OutputDelayMs,
    int RequiredLeadTimeMs,
    int MinBufferMs,
    IReadOnlyList<string> SupportedCommands);

public sealed record ArtworkChannelStateV2(string Source, string? Format, int? Width, int? Height);

public sealed record ArtworkStateV2(IReadOnlyList<ArtworkChannelStateV2> Channels);

public sealed record ClientStateV2(bool Available, PlayerStateV2? Player, ArtworkStateV2? Artwork);

public sealed record PairInit(int PairingIndex);

public sealed record PairFinalize(string LongTermPsk);

public sealed record PairAbort(string Reason);

public static class PairAbortReasons
{
    public const string MethodNotSupported = "method_not_supported";
    public const string AttemptTimeout = "attempt_timeout";
    public const string UserCancelled = "user_cancelled";
    public const string ConcurrentAttempt = "concurrent_attempt";
}

public static class GoodbyeReasonsV2
{
    public const string Unauthorized = "unauthorized";
    public const string PairingRequired = "pairing_required";
    public const string ConcurrentAttempt = "concurrent_attempt";
    public const string Unpaired = "unpaired";
}

public static class Activities
{
    public const string Playback = "playback";
    public const string Pairing = "pairing";
}

/// <summary>Builders for the messages of the encrypted protocol (the shared ones live in <see cref="MessageCodec"/>).</summary>
public static class MessageCodecV2
{
    public static string ClientHello(ClientHelloV2 hello) => MessageCodec.Serialize("client/hello", hello);

    public static string ClientState(ClientStateV2 state) => MessageCodec.Serialize("client/state", state);

    public static string PairInit(int pairingIndex) => MessageCodec.Serialize("client/pair-init", new PairInit(pairingIndex));

    public static string PairFinalize(string longTermPskBase64Url) => MessageCodec.Serialize("client/pair-finalize", new PairFinalize(longTermPskBase64Url));

    public static string PairAbort(string reason) => MessageCodec.Serialize("pair/abort", new PairAbort(reason));

    public static string ClientTime(long clientTransmitted) => MessageCodec.ClientTime(clientTransmitted);

    /// <summary>Truncates to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a character.</summary>
    public static string TruncateUtf8(string text, int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var result = new System.Text.StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (bytes + n > maxBytes)
            {
                break;
            }

            result.Append(rune.ToString());
            bytes += n;
        }

        return result.ToString();
    }

    internal static JsonSerializerOptions Options => WireJson.Options;
}
