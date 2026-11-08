namespace MaSatellite.Host.Presentation;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>The accent used when artwork gives no usable colour.</summary>
    public static Rgb Fallback { get; } = new(0x5B, 0x4A, 0xC8);

    /// <summary>A dark tone of the same hue, safe to sit behind white text.</summary>
    public Rgb ToBackdrop(double maxChannel = 0.34)
    {
        var peak = Math.Max(R, Math.Max(G, B)) / 255.0;
        var scale = peak <= 0 ? 1 : Math.Min(1, maxChannel / peak);
        // Lift very dark colours slightly so the gradient never collapses to flat black.
        const int floor = 14;
        return new Rgb(
            (byte)Math.Max(floor, (int)Math.Round(R * scale)),
            (byte)Math.Max(floor, (int)Math.Round(G * scale)),
            (byte)Math.Max(floor + 4, (int)Math.Round(B * scale)));
    }
}

/// <summary>Picks a representative colour from a small downscaled image (BGRA, 4 bytes per pixel).</summary>
public static class ArtworkColor
{
    public static Rgb Pick(ReadOnlySpan<byte> bgra)
    {
        double r = 0, g = 0, b = 0, total = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var pb = bgra[i];
            var pg = bgra[i + 1];
            var pr = bgra[i + 2];
            if (bgra[i + 3] < 128)
            {
                continue;
            }

            int max = Math.Max(pr, Math.Max(pg, pb));
            int min = Math.Min(pr, Math.Min(pg, pb));
            // Skip near-black and near-white pixels: they carry no identity.
            if (max < 40 || min > 225)
            {
                continue;
            }

            var saturation = (max - min) / (double)max;
            var weight = 0.05 + (saturation * saturation);
            r += pr * weight;
            g += pg * weight;
            b += pb * weight;
            total += weight;
        }

        return total <= 0
            ? Rgb.Fallback
            : new Rgb((byte)Math.Round(r / total), (byte)Math.Round(g / total), (byte)Math.Round(b / total));
    }
}
