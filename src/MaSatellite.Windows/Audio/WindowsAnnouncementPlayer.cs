using MaSatellite.Core.Audio;
using MaSatellite.Host.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

namespace MaSatellite.Windows.Audio;

/// <summary>Windows Media Foundation decoder that prepares announcement PCM for the real-time mixer.</summary>
public sealed class WindowsAnnouncementPlayer : IAudioAnnouncementPlayer
{
    private const int MaxDurationSeconds = 120;
    private readonly AnnouncementMixer _mixer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _decodeLock = new(1, 1);

    public WindowsAnnouncementPlayer(AnnouncementMixer mixer, ILogger? logger = null)
    {
        _mixer = mixer;
        _logger = logger ?? NullLogger<WindowsAnnouncementPlayer>.Instance;
    }

    public async Task PlayAsync(byte[] media, string contentType, int volumeLevel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.Length == 0 || media.Length > 30 * 1024 * 1024)
        {
            throw new ArgumentException("Announcement media must be between 1 byte and 30 MB.", nameof(media));
        }

        await _decodeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(Path.GetTempPath(), $"ma-satellite-announcement-{Guid.NewGuid():N}.audio");
        try
        {
            await File.WriteAllBytesAsync(path, media, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => DecodeAndQueue(path, volumeLevel), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Queued a {ContentType} announcement ({Bytes} bytes)", contentType, media.Length);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not remove temporary announcement file");
            }

            _decodeLock.Release();
        }
    }

    private void DecodeAndQueue(string path, int volumeLevel)
    {
        var format = _mixer.OutputFormat;
        if (format.SampleRate <= 0 || format.Channels <= 0)
        {
            throw new InvalidOperationException("The audio output has not been initialized yet.");
        }

        using var reader = new MediaFoundationReader(path);
        var target = new WaveFormat(format.SampleRate, 16, format.Channels);
        using var resampler = new MediaFoundationResampler(reader, target) { ResamplerQuality = 60 };
        var samples = resampler.ToSampleProvider();
        var block = new float[Math.Max(1024, format.SampleRate * format.Channels / 10)];
        var decoded = new List<float>(block.Length * 4);
        var maximumSamples = format.SampleRate * format.Channels * MaxDurationSeconds;

        while (true)
        {
            var read = samples.Read(block, 0, block.Length);
            if (read <= 0)
            {
                break;
            }

            if (decoded.Count + read > maximumSamples)
            {
                throw new InvalidOperationException($"Announcement exceeds the {MaxDurationSeconds}-second limit.");
            }

            for (var i = 0; i < read; i++)
            {
                decoded.Add(block[i]);
            }
        }

        if (decoded.Count == 0)
        {
            throw new InvalidOperationException("The announcement contained no decodable audio.");
        }

        _mixer.Enqueue(CollectionsMarshal.AsSpan(decoded), format.SampleRate, format.Channels, volumeLevel);
    }
}
