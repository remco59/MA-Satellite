using MaSatellite.Core.Protocol;

namespace MaSatellite.Core.Audio;

public static class AudioDecoderFactory
{
    public static IAudioDecoder Create(StreamPlayerFormat format)
    {
        var fmt = new AudioStreamFormat(format.SampleRate, format.Channels, format.BitDepth);
        switch (format.Codec)
        {
            case "pcm":
                return new PcmDecoder(fmt);
            case "flac":
                var header = string.IsNullOrEmpty(format.CodecHeader) ? [] : Convert.FromBase64String(format.CodecHeader);
                return new FlacDecoder(fmt, header);
            default:
                throw new AudioDecodeException($"Unsupported codec '{format.Codec}'.");
        }
    }
}
