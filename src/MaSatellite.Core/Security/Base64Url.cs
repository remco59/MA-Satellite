namespace MaSatellite.Core.Security;

/// <summary>base64url without padding, as Sendspin uses for keys, ids and handshake data.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Throws <see cref="FormatException"/> for anything that is not valid unpadded base64url.</summary>
    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2:
                s += "==";
                break;
            case 3:
                s += "=";
                break;
            case 1:
                throw new FormatException("Invalid base64url length.");
        }

        return Convert.FromBase64String(s);
    }

    public static bool TryDecode(string? text, int expectedLength, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            var decoded = Decode(text);
            if (decoded.Length != expectedLength)
            {
                return false;
            }

            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
