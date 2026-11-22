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
    private readonly object _decodeGate = new();

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

        var extension = GetFileExtension(media, contentType);
        var path = Path.Combine(Path.GetTempPath(), $"ma-satellite-announcement-{Guid.NewGuid():N}{extension}");
        try
        {
            await File.WriteAllBytesAsync(path, media, cancellationToken).ConfigureAwait(false);
            await Task.Run(() =>
            {
                lock (_decodeGate)
                {
                    DecodeAndQueue(path, volumeLevel);
                }
            }, cancellationToken).ConfigureAwait(false);
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

        }
    }

    private static string GetFileExtension(byte[] media, string contentType)
    {
        var mime = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        var byType = mime switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" or "audio/vnd.wave" => ".wav",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/aac" or "audio/x-aac" => ".aac",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/ogg" or "audio/opus" => ".ogg",
            "audio/webm" => ".webm",
            "audio/x-ms-wma" or "audio/wma" => ".wma",
            _ => string.Empty,
        };
        if (byType.Length > 0)
        {
            return byType;
        }

        if (media.Length >= 12 && media.AsSpan(0, 4).SequenceEqual("RIFF"u8) && media.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return ".wav";
        }

        if (media.Length >= 4 && media.AsSpan(0, 4).SequenceEqual("fLaC"u8))
        {
            return ".flac";
        }

        if (media.Length >= 4 && media.AsSpan(0, 4).SequenceEqual("OggS"u8))
        {
            return ".ogg";
        }

        if (media.Length >= 8 && media.AsSpan(4, 4).SequenceEqual("ftyp"u8))
        {
            return ".m4a";
        }

        return ".mp3";
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

        _mixer.Enqueue(decoded.ToArray(), format.SampleRate, format.Channels, volumeLevel);
    }
}
