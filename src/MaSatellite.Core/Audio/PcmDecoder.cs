namespace MaSatellite.Core.Audio;

/// <summary>Little-endian signed integer PCM (24-bit packed as 3 bytes), as defined by the Sendspin spec.</summary>
public sealed class PcmDecoder : IAudioDecoder
{
    public PcmDecoder(AudioStreamFormat format)
    {
        if (!format.IsValid || format.BitDepth is not (16 or 24 or 32 or 8))
        {
            throw new AudioDecodeException($"Unsupported PCM format {format}.");
        }

        Format = format;
    }

    public AudioStreamFormat Format { get; }

    public float[] Decode(ReadOnlySpan<byte> data)
    {
        var bytesPerSample = Format.BitDepth / 8;
        var frameBytes = bytesPerSample * Format.Channels;
        var usable = data.Length - (data.Length % frameBytes);
        var count = usable / bytesPerSample;
        var result = new float[count];
        switch (Format.BitDepth)
        {
            case 8:
                for (var i = 0; i < count; i++)
                {
                    result[i] = (data[i] - 128) / 128f;
                }

                break;
            case 16:
                for (var i = 0; i < count; i++)
                {
                    result[i] = (short)(data[2 * i] | (data[(2 * i) + 1] << 8)) / 32768f;
                }

                break;
            case 24:
                for (var i = 0; i < count; i++)
                {
                    var o = 3 * i;
                    var v = data[o] | (data[o + 1] << 8) | (data[o + 2] << 16);
                    v = (v << 8) >> 8; // sign extend
                    result[i] = v / 8388608f;
                }

                break;
            default:
                for (var i = 0; i < count; i++)
                {
                    var o = 4 * i;
                    var v = data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24);
                    result[i] = v / 2147483648f;
                }

                break;
        }

        return result;
    }
}
