namespace MaSatellite.Core.Security;

/// <summary>
/// The version-0 pairing token the operator enters into the server: <c>"SP:0"</c> plus the base32 (RFC 4648, unpadded, <c>2</c> written
/// as <c>9</c>) encoding of <c>client_key (32 bytes) || pairing_psk (32 bytes)</c>.
/// </summary>
public static class PairingToken
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Create(byte[] clientPublicKey, byte[] pairingPsk)
    {
        if (clientPublicKey.Length != 32 || pairingPsk.Length != 32)
        {
            throw new ArgumentException("Client key and pairing PSK must be 32 bytes each.");
        }

        return "SP:0" + Base32Encode([.. clientPublicKey, .. pairingPsk]).Replace('2', '9');
    }

    /// <summary>Parses a token leniently (case, whitespace, optional <c>SP:</c>); returns false for anything malformed.</summary>
    public static bool TryParse(string? text, out byte[] clientPublicKey, out byte[] pairingPsk)
    {
        clientPublicKey = [];
        pairingPsk = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim().ToUpperInvariant();
        if (t.StartsWith("SP:", StringComparison.Ordinal))
        {
            t = t[3..];
        }

        if (t.Length < 2 || t[0] != '0')
        {
            return false;
        }

        var body = t[1..].Replace('9', '2');
        if (!TryBase32Decode(body, out var payload) || payload.Length < 64)
        {
            return false;
        }

        clientPublicKey = payload[..32];
        pairingPsk = payload[32..64];
        return true;
    }

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder((data.Length * 8 / 5) + 1);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    private static bool TryBase32Decode(string text, out byte[] bytes)
    {
        var result = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var c in text)
        {
            var v = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0)
            {
                bytes = [];
                return false;
            }

            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        bytes = [.. result];
        return true;
    }
}
