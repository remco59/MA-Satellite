namespace MaSatellite.Core.Audio;

public readonly record struct AudioOutputFormat(int SampleRate, int Channels);

/// <summary>Called on the audio thread to fill an interleaved float buffer. <paramref name="playoutLocalMicros"/> is when its first frame is audible.</summary>
public delegate void AudioRenderCallback(Span<float> buffer, long playoutLocalMicros);

public interface IAudioOutput : IDisposable
{
    /// <summary>Opens the output device and starts pulling audio. Returns the device format. Throws if no device can be opened.</summary>
    AudioOutputFormat Start(AudioRenderCallback render);

    void Stop();

    /// <summary>Raised when the device disappears or fails; the owner may call <see cref="Start"/> again.</summary>
    event EventHandler<Exception?>? Failed;
}
