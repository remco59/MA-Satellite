namespace MaSatellite.Core.Security.Noise;

/// <summary>The two Noise cipher suites Sendspin defines (DH is always Curve25519, hash always SHA-256).</summary>
public enum NoiseSuite
{
    /// <summary><c>25519_ChaChaPoly_SHA256</c>: the software-friendly suite.</summary>
    ChaChaPolySha256,

    /// <summary><c>25519_AESGCM_SHA256</c>: the hardware-accelerated suite.</summary>
    AesGcmSha256,
}

public static class NoiseSuiteNames
{
    public static string SuiteName(this NoiseSuite suite) => suite switch
    {
        NoiseSuite.ChaChaPolySha256 => "25519_ChaChaPoly_SHA256",
        NoiseSuite.AesGcmSha256 => "25519_AESGCM_SHA256",
        _ => throw new ArgumentOutOfRangeException(nameof(suite)),
    };

    /// <summary>The full Noise protocol name for the Sendspin pattern, e.g. <c>Noise_KKpsk2_25519_ChaChaPoly_SHA256</c>.</summary>
    public static string ProtocolName(this NoiseSuite suite) => "Noise_KKpsk2_" + suite.SuiteName();

    public static bool TryParse(string? name, out NoiseSuite suite)
    {
        switch (name)
        {
            case "25519_ChaChaPoly_SHA256":
                suite = NoiseSuite.ChaChaPolySha256;
                return true;
            case "25519_AESGCM_SHA256":
                suite = NoiseSuite.AesGcmSha256;
                return true;
            default:
                suite = default;
                return false;
        }
    }
}
