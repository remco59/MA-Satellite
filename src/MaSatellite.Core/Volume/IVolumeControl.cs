namespace MaSatellite.Core.Volume;

/// <summary>The player's volume and mute, as seen by Music Assistant (0-100). On Windows this is the master volume.</summary>
public interface IVolumeControl
{
    int Volume { get; }

    bool Muted { get; }

    void SetVolume(int volume);

    void SetMuted(bool muted);

    /// <summary>Raised when volume or mute changed, whether by us or by the user in Windows.</summary>
    event Action? Changed;
}

/// <summary>In-memory volume, for tests and platforms without a system mixer.</summary>
public sealed class InMemoryVolumeControl : IVolumeControl
{
    private int _volume = 50;
    private bool _muted;

    public int Volume => _volume;

    public bool Muted => _muted;

    public event Action? Changed;

    public void SetVolume(int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (volume != _volume)
        {
            _volume = volume;
            Changed?.Invoke();
        }
    }

    public void SetMuted(bool muted)
    {
        if (muted != _muted)
        {
            _muted = muted;
            Changed?.Invoke();
        }
    }
}
