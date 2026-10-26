namespace MaSatellite.Core.Audio;

public readonly record struct AudioStreamFormat(int SampleRate, int Channels, int BitDepth)
{
    public bool IsValid => SampleRate is >= 8000 and <= 384000 && Channels is >= 1 and <= 8 && BitDepth is 8 or 16 or 20 or 24 or 32;
}
