using System.Buffers.Binary;

namespace MaSatellite.Core.Protocol;

public static class BinaryMessageTypes
{
    public const byte Audio = 4;
    public const byte ArtworkChannel0 = 8;
    public const byte ArtworkChannel3 = 11;

    public static bool IsArtwork(byte type) => type is >= ArtworkChannel0 and <= ArtworkChannel3;
}

/// <summary>A binary message: <c>[type:1][timestamp:8 big-endian][payload]</c>.</summary>
public readonly record struct BinaryFrame(byte Type, long TimestampMicros, ReadOnlyMemory<byte> Payload)
{
    public const int HeaderLength = 9;

    public static bool TryParse(ReadOnlyMemory<byte> data, out BinaryFrame frame)
    {
        if (data.Length < HeaderLength)
        {
            frame = default;
            return false;
        }

        var span = data.Span;
        frame = new BinaryFrame(span[0], BinaryPrimitives.ReadInt64BigEndian(span.Slice(1, 8)), data[HeaderLength..]);
        return true;
    }

    public int ArtworkChannel => Type - BinaryMessageTypes.ArtworkChannel0;
}
