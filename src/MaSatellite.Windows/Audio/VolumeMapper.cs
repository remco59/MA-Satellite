namespace MaSatellite.Windows.Audio;

/// <summary>
/// Converts between Sendspin volume (0-100) and the Windows endpoint scalar (0.0-1.0). Windows already applies its
/// own perceptual taper to the scalar, so the mapping is the identity: MA's slider position is the Windows slider position.
/// </summary>
internal static class VolumeMapper
{
    public static int ToPercent(float scalar) => Math.Clamp((int)Math.Round(scalar * 100f, MidpointRounding.AwayFromZero), 0, 100);

    public static float ToScalar(int percent) => Math.Clamp(percent, 0, 100) / 100f;
}
