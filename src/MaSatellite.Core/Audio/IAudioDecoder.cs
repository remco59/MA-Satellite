namespace MaSatellite.Core.Audio;

public sealed class AudioDecodeException : Exception
{
    public AudioDecodeException(string message)
        : base(message)
    {
    }

    public AudioDecodeException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Turns one encoded chunk from the server into interleaved float samples in [-1, 1].</summary>
public interface IAudioDecoder
{
    AudioStreamFormat Format { get; }

    /// <summary>Decodes a whole chunk; the result length is a multiple of <see cref="AudioStreamFormat.Channels"/>.</summary>
    float[] Decode(ReadOnlySpan<byte> data);
}
