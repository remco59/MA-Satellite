namespace MaSatellite.Core.Audio;

/// <summary>
/// Managed FLAC frame decoder for Sendspin chunks. The stream header (<c>codec_header</c>) is optional; chunks contain
/// complete FLAC frames. Supports 8–24 bit, up to 8 channels, all subframe types and stereo decorrelation modes.
/// </summary>
public sealed class FlacDecoder : IAudioDecoder
{
    private static readonly int[] SampleRates = [0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000, 0, 0, 0, 0];
    private static readonly ushort[] Crc16Table = BuildCrc16();

    private readonly int[][] _channelBuffers;
    private readonly int[] _residual = new int[65536];

    public FlacDecoder(AudioStreamFormat fallbackFormat, ReadOnlySpan<byte> codecHeader)
    {
        Format = TryReadStreamInfo(codecHeader, out var info) ? info : fallbackFormat;
        if (!Format.IsValid || Format.BitDepth > 24)
        {
            throw new AudioDecodeException($"Unsupported FLAC format {Format}.");
        }

        _channelBuffers = new int[8][];
        for (var i = 0; i < _channelBuffers.Length; i++)
        {
            _channelBuffers[i] = new int[65536];
        }
    }

    public AudioStreamFormat Format { get; }

    public float[] Decode(ReadOnlySpan<byte> data)
    {
        var output = new List<float[]>();
        var total = 0;
        var pos = 0;
        while (pos < data.Length)
        {
            var frame = DecodeFrame(data[pos..], out var consumed);
            output.Add(frame);
            total += frame.Length;
            pos += consumed;
        }

        if (output.Count == 1)
        {
            return output[0];
        }

        var result = new float[total];
        var offset = 0;
        foreach (var f in output)
        {
            f.CopyTo(result, offset);
            offset += f.Length;
        }

        return result;
    }

    private static bool TryReadStreamInfo(ReadOnlySpan<byte> header, out AudioStreamFormat format)
    {
        format = default;
        var pos = 0;
        if (header.Length >= 4 && header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C')
        {
            pos = 4;
        }
        else if (header.Length != 34)
        {
            return false;
        }

        while (pos < header.Length)
        {
            if (pos == 0)
            {
                // bare STREAMINFO body without block header
                return ParseStreamInfo(header, out format);
            }

            if (header.Length - pos < 4)
            {
                return false;
            }

            var last = (header[pos] & 0x80) != 0;
            var type = header[pos] & 0x7F;
            var length = (header[pos + 1] << 16) | (header[pos + 2] << 8) | header[pos + 3];
            pos += 4;
            if (header.Length - pos < length)
            {
                return false;
            }

            if (type == 0)
            {
                return ParseStreamInfo(header.Slice(pos, length), out format);
            }

            pos += length;
            if (last)
            {
                break;
            }
        }

        return false;
    }

    private static bool ParseStreamInfo(ReadOnlySpan<byte> body, out AudioStreamFormat format)
    {
        format = default;
        if (body.Length < 18)
        {
            return false;
        }

        // 16+16 blocksizes, 24+24 framesizes, then 20 bits rate, 3 bits channels-1, 5 bits bps-1
        var rate = (body[10] << 12) | (body[11] << 4) | (body[12] >> 4);
        var channels = ((body[12] >> 1) & 0x7) + 1;
        var bps = (((body[12] & 1) << 4) | (body[13] >> 4)) + 1;
        format = new AudioStreamFormat(rate, channels, bps);
        return rate > 0;
    }

    private float[] DecodeFrame(ReadOnlySpan<byte> data, out int consumed)
    {
        var br = new BitReader(data);
        if (br.Read(14) != 0x3FFE)
        {
            throw new AudioDecodeException("FLAC frame sync code not found.");
        }

        br.Read(1); // reserved
        br.Read(1); // blocking strategy (fixed/variable); the frame number is not needed
        var blockSizeCode = (int)br.Read(4);
        var sampleRateCode = (int)br.Read(4);
        var channelAssignment = (int)br.Read(4);
        var sampleSizeCode = (int)br.Read(3);
        br.Read(1); // reserved

        ReadUtf8Number(ref br);

        int blockSize = blockSizeCode switch
        {
            0 => throw new AudioDecodeException("Reserved FLAC block size."),
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (int)br.Read(8) + 1,
            7 => (int)br.Read(16) + 1,
            _ => 256 << (blockSizeCode - 8),
        };

        var sampleRate = sampleRateCode switch
        {
            0 => Format.SampleRate,
            12 => (int)br.Read(8) * 1000,
            13 => (int)br.Read(16),
            14 => (int)br.Read(16) * 10,
            15 => throw new AudioDecodeException("Invalid FLAC sample rate code."),
            _ => SampleRates[sampleRateCode],
        };

        var bps = sampleSizeCode switch
        {
            0 => Format.BitDepth,
            1 => 8,
            2 => 12,
            4 => 16,
            5 => 20,
            6 => 24,
            7 => 32,
            _ => throw new AudioDecodeException("Reserved FLAC sample size."),
        };

        br.Read(8); // header CRC-8 (the frame CRC-16 below covers the whole frame)

        if (bps > 24)
        {
            throw new AudioDecodeException("FLAC bit depth above 24 is not supported.");
        }

        var channels = channelAssignment <= 7 ? channelAssignment + 1 : 2;
        if (channelAssignment is > 10 and <= 15)
        {
            throw new AudioDecodeException("Reserved FLAC channel assignment.");
        }

        if (channels != Format.Channels)
        {
            throw new AudioDecodeException($"FLAC frame has {channels} channels but the stream has {Format.Channels}.");
        }

        if (blockSize > 65535)
        {
            throw new AudioDecodeException("FLAC block size too large.");
        }

        _ = sampleRate;
        for (var ch = 0; ch < channels; ch++)
        {
            var subframeBps = bps;
            if ((channelAssignment == 8 && ch == 1) || (channelAssignment == 9 && ch == 0) || (channelAssignment == 10 && ch == 1))
            {
                subframeBps++;
            }

            DecodeSubframe(ref br, _channelBuffers[ch], blockSize, subframeBps);
        }

        br.AlignToByte();
        var crcPos = br.BytePosition;
        var expected = br.Read(16);
        consumed = br.BytePosition;

        var crc = 0;
        for (var i = 0; i < crcPos; i++)
        {
            crc = ((crc << 8) & 0xFFFF) ^ Crc16Table[((crc >> 8) ^ data[i]) & 0xFF];
        }

        if (crc != expected)
        {
            throw new AudioDecodeException("FLAC frame CRC mismatch.");
        }

        return Interleave(channelAssignment, channels, blockSize, bps);
    }

    private float[] Interleave(int assignment, int channels, int blockSize, int bps)
    {
        var scale = 1f / (1 << (bps - 1));
        var output = new float[blockSize * channels];
        var c0 = _channelBuffers[0];
        var c1 = _channelBuffers[1];
        switch (assignment)
        {
            case 8: // left/side
                for (var i = 0; i < blockSize; i++)
                {
                    var l = c0[i];
                    var r = l - c1[i];
                    output[2 * i] = l * scale;
                    output[(2 * i) + 1] = r * scale;
                }

                break;
            case 9: // side/right
                for (var i = 0; i < blockSize; i++)
                {
                    var r = c1[i];
                    var l = c0[i] + r;
                    output[2 * i] = l * scale;
                    output[(2 * i) + 1] = r * scale;
                }

                break;
            case 10: // mid/side
                for (var i = 0; i < blockSize; i++)
                {
                    var side = c1[i];
                    var mid = (c0[i] << 1) | (side & 1);
                    output[2 * i] = ((mid + side) >> 1) * scale;
                    output[(2 * i) + 1] = ((mid - side) >> 1) * scale;
                }

                break;
            default:
                for (var ch = 0; ch < channels; ch++)
                {
                    var src = _channelBuffers[ch];
                    for (var i = 0; i < blockSize; i++)
                    {
                        output[(i * channels) + ch] = src[i] * scale;
                    }
                }

                break;
        }

        return output;
    }

    private static void ReadUtf8Number(ref BitReader br)
    {
        var first = (int)br.Read(8);
        var extra = first switch
        {
            < 0x80 => 0,
            < 0xC0 => throw new AudioDecodeException("Invalid FLAC frame number."),
            < 0xE0 => 1,
            < 0xF0 => 2,
            < 0xF8 => 3,
            < 0xFC => 4,
            < 0xFE => 5,
            _ => 6,
        };

        for (var i = 0; i < extra; i++)
        {
            br.Read(8);
        }
    }

    private void DecodeSubframe(ref BitReader br, int[] output, int blockSize, int bps)
    {
        if (br.Read(1) != 0)
        {
            throw new AudioDecodeException("FLAC subframe padding bit set.");
        }

        var type = (int)br.Read(6);
        var wasted = 0;
        if (br.Read(1) == 1)
        {
            wasted = 1;
            while (br.Read(1) == 0)
            {
                wasted++;
            }
        }

        var effectiveBps = bps - wasted;
        if (effectiveBps <= 0 || effectiveBps > 25)
        {
            throw new AudioDecodeException("Invalid FLAC subframe bit depth.");
        }

        if (type == 0)
        {
            var v = br.ReadSigned(effectiveBps);
            Array.Fill(output, v, 0, blockSize);
        }
        else if (type == 1)
        {
            for (var i = 0; i < blockSize; i++)
            {
                output[i] = br.ReadSigned(effectiveBps);
            }
        }
        else if (type is >= 8 and <= 12)
        {
            var order = type - 8;
            for (var i = 0; i < order; i++)
            {
                output[i] = br.ReadSigned(effectiveBps);
            }

            ReadResidual(ref br, blockSize, order);
            RestoreFixed(output, order, blockSize);
        }
        else if (type >= 32)
        {
            var order = type - 31;
            if (order > blockSize)
            {
                throw new AudioDecodeException("FLAC LPC order exceeds block size.");
            }

            for (var i = 0; i < order; i++)
            {
                output[i] = br.ReadSigned(effectiveBps);
            }

            var precision = (int)br.Read(4) + 1;
            if (precision == 16)
            {
                throw new AudioDecodeException("Invalid FLAC LPC precision.");
            }

            var shift = br.ReadSigned(5);
            if (shift < 0)
            {
                throw new AudioDecodeException("Negative FLAC LPC shift is not supported.");
            }

            Span<int> coefs = stackalloc int[order];
            for (var i = 0; i < order; i++)
            {
                coefs[i] = br.ReadSigned(precision);
            }

            ReadResidual(ref br, blockSize, order);
            for (var i = order; i < blockSize; i++)
            {
                long sum = 0;
                for (var j = 0; j < order; j++)
                {
                    sum += (long)coefs[j] * output[i - 1 - j];
                }

                output[i] = (int)(sum >> shift) + _residual[i];
            }
        }
        else
        {
            throw new AudioDecodeException("Reserved FLAC subframe type.");
        }

        if (wasted > 0)
        {
            for (var i = 0; i < blockSize; i++)
            {
                output[i] <<= wasted;
            }
        }
    }

    private void RestoreFixed(int[] x, int order, int blockSize)
    {
        var r = _residual;
        switch (order)
        {
            case 0:
                for (var i = 0; i < blockSize; i++)
                {
                    x[i] = r[i];
                }

                break;
            case 1:
                for (var i = 1; i < blockSize; i++)
                {
                    x[i] = r[i] + x[i - 1];
                }

                break;
            case 2:
                for (var i = 2; i < blockSize; i++)
                {
                    x[i] = r[i] + (2 * x[i - 1]) - x[i - 2];
                }

                break;
            case 3:
                for (var i = 3; i < blockSize; i++)
                {
                    x[i] = r[i] + (3 * x[i - 1]) - (3 * x[i - 2]) + x[i - 3];
                }

                break;
            default:
                for (var i = 4; i < blockSize; i++)
                {
                    x[i] = r[i] + (4 * x[i - 1]) - (6 * x[i - 2]) + (4 * x[i - 3]) - x[i - 4];
                }

                break;
        }
    }

    /// <summary>Reads the residual into <c>_residual[order..blockSize)</c>.</summary>
    private void ReadResidual(ref BitReader br, int blockSize, int order)
    {
        var method = (int)br.Read(2);
        if (method > 1)
        {
            throw new AudioDecodeException("Reserved FLAC residual coding method.");
        }

        var paramBits = method == 0 ? 4 : 5;
        var escape = method == 0 ? 15 : 31;
        var partitionOrder = (int)br.Read(4);
        var partitions = 1 << partitionOrder;
        if ((blockSize >> partitionOrder) < order || (blockSize & (partitions - 1)) != 0)
        {
            throw new AudioDecodeException("Invalid FLAC partition order.");
        }

        var pos = order;
        for (var p = 0; p < partitions; p++)
        {
            var count = (blockSize >> partitionOrder) - (p == 0 ? order : 0);
            var k = (int)br.Read(paramBits);
            if (k == escape)
            {
                var bits = (int)br.Read(5);
                for (var i = 0; i < count; i++)
                {
                    _residual[pos++] = bits == 0 ? 0 : br.ReadSigned(bits);
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var q = br.ReadUnary();
                    var u = (q << k) | (k == 0 ? 0 : (int)br.Read(k));
                    _residual[pos++] = (u >> 1) ^ -(u & 1);
                }
            }
        }
    }

    private static ushort[] BuildCrc16()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i << 8;
            for (var b = 0; b < 8; b++)
            {
                c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x8005) : (c << 1);
            }

            table[i] = (ushort)(c & 0xFFFF);
        }

        return table;
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bitPos;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bitPos = 0;
        }

        public readonly int BytePosition => _bitPos >> 3;

        public uint Read(int bits)
        {
            if (bits == 0)
            {
                return 0;
            }

            if (_bitPos + bits > _data.Length * 8)
            {
                throw new AudioDecodeException("Unexpected end of FLAC data.");
            }

            ulong value = 0;
            var remaining = bits;
            while (remaining > 0)
            {
                var byteIndex = _bitPos >> 3;
                var bitOffset = _bitPos & 7;
                var take = Math.Min(8 - bitOffset, remaining);
                var current = (_data[byteIndex] >> (8 - bitOffset - take)) & ((1 << take) - 1);
                value = (value << take) | (uint)current;
                _bitPos += take;
                remaining -= take;
            }

            return (uint)value;
        }

        public int ReadSigned(int bits)
        {
            var v = (int)Read(bits);
            return bits == 32 ? v : (v << (32 - bits)) >> (32 - bits);
        }

        public int ReadUnary()
        {
            var n = 0;
            while (Read(1) == 0)
            {
                n++;
                if (n > 1 << 24)
                {
                    throw new AudioDecodeException("Runaway unary code in FLAC data.");
                }
            }

            return n;
        }

        public void AlignToByte() => _bitPos = (_bitPos + 7) & ~7;
    }
}
