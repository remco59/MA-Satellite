using MaSatellite.Core.Audio;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;

namespace MaSatellite.Core.Tests;

public class DecoderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Splits a .flac file into the stream header (fLaC + metadata blocks) and the frame data.</summary>
    private static (byte[] Header, byte[] Frames) SplitFlac(byte[] file)
    {
        var pos = 4;
        while (true)
        {
            var last = (file[pos] & 0x80) != 0;
            var len = (file[pos + 1] << 16) | (file[pos + 2] << 8) | file[pos + 3];
            pos += 4 + len;
            if (last)
            {
                break;
            }
        }

        return (file[..pos], file[pos..]);
    }

    public static IEnumerable<object[]> FlacCases() =>
    [
        ["s16_44k_st_l0", 44100, 2, 16],
        ["s16_44k_st_l5", 44100, 2, 16],
        ["s16_44k_st_l8", 44100, 2, 16],
        ["s16_44k_st_leftside", 44100, 2, 16],
        ["s16_44k_st_rightside", 44100, 2, 16],
        ["s16_44k_st_midside", 44100, 2, 16],
        ["s16_44k_st_indep", 44100, 2, 16],
        ["s16_48k_mono_l5", 48000, 1, 16],
        ["s16_44k_st_blk1000", 44100, 2, 16],
        ["s16_44k_st_blk4608", 44100, 2, 16],
        ["s24_48k_st_l8", 48000, 2, 24],
        ["s16_96k_st_l5", 96000, 2, 16],
    ];

    [Theory]
    [MemberData(nameof(FlacCases))]
    public void Flac_decodes_bit_exactly(string name, int rate, int channels, int bits)
    {
        var (header, frames) = SplitFlac(File.ReadAllBytes(Fixture(name + ".flac")));
        var reference = File.ReadAllBytes(Fixture(name + ".pcm32"));

        // The header gives the format; the fallback is deliberately wrong to prove that.
        var decoder = new FlacDecoder(new AudioStreamFormat(8000, 1, 8), header);
        Assert.Equal(new AudioStreamFormat(rate, channels, bits), decoder.Format);

        var samples = decoder.Decode(frames);

        Assert.Equal(reference.Length / 4, samples.Length);
        var scale = 1 << (bits - 1);
        for (var i = 0; i < samples.Length; i++)
        {
            var expected = BitConverter.ToInt32(reference, i * 4);
            var actual = (int)Math.Round(samples[i] * scale);
            if (expected != actual)
            {
                Assert.Fail($"{name}: sample {i} expected {expected} but was {actual}");
            }
        }
    }

    [Fact]
    public void Flac_without_header_uses_stream_start_values()
    {
        var (_, frames) = SplitFlac(File.ReadAllBytes(Fixture("s16_44k_st_l5.flac")));
        var decoder = AudioDecoderFactory.Create(new StreamPlayerFormat("flac", 44100, 2, 16, null));
        Assert.Equal(44100 * 3 / 10 * 2, decoder.Decode(frames).Length);
    }

    [Fact]
    public void Flac_header_via_factory_is_base64_decoded()
    {
        var (header, frames) = SplitFlac(File.ReadAllBytes(Fixture("s16_48k_mono_l5.flac")));
        var decoder = AudioDecoderFactory.Create(new StreamPlayerFormat("flac", 48000, 1, 16, Convert.ToBase64String(header)));
        Assert.Equal(14400, decoder.Decode(frames).Length);
    }

    [Fact]
    public void Flac_corrupt_frame_is_rejected_by_crc()
    {
        var (header, frames) = SplitFlac(File.ReadAllBytes(Fixture("s16_44k_st_l5.flac")));
        frames[frames.Length / 2] ^= 0x55;
        var decoder = new FlacDecoder(default, header);
        Assert.Throws<AudioDecodeException>(() => decoder.Decode(frames));
    }

    [Fact]
    public void Flac_truncated_frame_throws()
    {
        var (header, frames) = SplitFlac(File.ReadAllBytes(Fixture("s16_44k_st_l5.flac")));
        var decoder = new FlacDecoder(default, header);
        Assert.Throws<AudioDecodeException>(() => decoder.Decode(frames.AsSpan(0, 100)));
    }

    [Fact]
    public void Default_Sendspin_formats_negotiate_PCM_only_until_real_FLAC_is_fixed()
    {
        // A Music Assistant server may choose any advertised format, not just the first.
        // PCM-only avoids the known real-server FLAC frame-sync failure (silent output).
        var formats = new SendspinOptions().SupportedFormats;

        Assert.Equal(2, formats.Count);
        Assert.All(formats, format =>
        {
            Assert.Equal("pcm", format.Codec);
            Assert.Equal(2, format.Channels);
            Assert.Equal(16, format.BitDepth);
        });
        Assert.Equal(48000, formats[0].SampleRate);
        Assert.Equal(44100, formats[1].SampleRate);
        foreach (var format in formats)
        {
            Assert.IsType<PcmDecoder>(AudioDecoderFactory.Create(
                new StreamPlayerFormat(format.Codec, format.SampleRate, format.Channels, format.BitDepth, null)));
        }
    }

    [Fact]
    public void Pcm16_round_trip()
    {
        var decoder = new PcmDecoder(new AudioStreamFormat(48000, 2, 16));
        short[] src = [0, 1, -1, 32767, -32768, 1000];
        var bytes = new byte[src.Length * 2];
        Buffer.BlockCopy(src, 0, bytes, 0, bytes.Length);
        var f = decoder.Decode(bytes);
        Assert.Equal(6, f.Length);
        Assert.Equal(0f, f[0]);
        Assert.Equal(32767 / 32768f, f[3], 6);
        Assert.Equal(-1f, f[4]);
    }

    [Fact]
    public void Pcm24_is_sign_extended_and_packed()
    {
        var decoder = new PcmDecoder(new AudioStreamFormat(48000, 1, 24));
        var f = decoder.Decode([0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF]);
        Assert.Equal([-1f, 8388607 / 8388608f, -1 / 8388608f], f);
    }

    [Fact]
    public void Pcm_drops_trailing_partial_frame()
    {
        var decoder = new PcmDecoder(new AudioStreamFormat(44100, 2, 16));
        Assert.Equal(2, decoder.Decode(new byte[7]).Length);
    }

    [Fact]
    public void Factory_rejects_unknown_codec()
    {
        Assert.Throws<AudioDecodeException>(() => AudioDecoderFactory.Create(new StreamPlayerFormat("opus", 48000, 2, 16, null)));
    }
}
