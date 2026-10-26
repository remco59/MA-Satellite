using System.Security.Cryptography;
using System.Text;

namespace MaSatellite.Core.Security;

/// <summary>PSK categories as sent in the first handshake message.</summary>
public static class PskCategory
{
    public const string LongTerm = "lt";
    public const string Pairing = "pr";
    public const string Sentinel = "sn";

    public static bool IsKnown(string? category) => category is LongTerm or Pairing or Sentinel;
}

/// <summary>PSK identifiers and the published Sentinel constant.</summary>
public static class SendspinPsk
{
    private static readonly byte[] IdLabel = Encoding.UTF8.GetBytes("sendspin-psk-id-v1");

    /// <summary>The published constant used whenever no other PSK applies: <c>SHA-256("sendspin-sentinel-psk-v1")</c>.</summary>
    public static byte[] Sentinel { get; } = SHA256.HashData(Encoding.UTF8.GetBytes("sendspin-sentinel-psk-v1"));

    public static string SentinelId { get; } = Id(Sentinel);

    /// <summary><c>base64url(SHA-256("sendspin-psk-id-v1" || PSK))</c>, 43 characters.</summary>
    public static string Id(ReadOnlySpan<byte> psk) => Base64Url.Encode(SHA256.HashData([.. IdLabel, .. psk]));

    public static byte[] NewRandom() => RandomNumberGenerator.GetBytes(32);
}
