using System.Buffers.Binary;
using MaSatellite.Core.Session;

namespace MaSatellite.Core.Tests;

public class ArtworkAssemblerTests
{
    private static byte[] Announce(int channel, long ts, uint total)
    {
        var m = new byte[14];
        m[0] = (byte)(8 + channel);
        m[1] = 2;
        BinaryPrimitives.WriteInt64BigEndian(m.AsSpan(2, 8), ts);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(10, 4), total);
        return m;
    }

    private static byte[] Part(int channel, params byte[] data) => [(byte)(8 + channel), 0, .. data];

    private static byte[] Cancel(int channel) => [(byte)(8 + channel), 1];

    [Fact]
    public void An_image_completes_when_all_parts_have_arrived()
    {
        var a = new ArtworkAssembler();
        Assert.Equal(ArtworkResult.Ok, a.Process(0, Announce(0, 5_000, 5), out _));
        Assert.Equal(ArtworkResult.Ok, a.Process(0, Part(0, 1, 2), out _));
        Assert.Equal(ArtworkResult.Completed, a.Process(0, Part(0, 3, 4, 5), out var image));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, image!.Data);
        Assert.Equal(5_000, image.TimestampMicros);
        Assert.Equal(0, image.Channel);
    }

    [Fact]
    public void A_zero_size_announce_clears_immediately()
    {
        var a = new ArtworkAssembler();
        Assert.Equal(ArtworkResult.Completed, a.Process(1, Announce(1, 9, 0), out var image));
        Assert.Empty(image!.Data);
        Assert.Equal(1, image.Channel);
    }

    [Fact]
    public void Cancel_discards_the_transfer_and_allows_a_new_announce()
    {
        var a = new ArtworkAssembler();
        a.Process(0, Announce(0, 1, 4), out _);
        a.Process(0, Part(0, 1), out _);
        Assert.Equal(ArtworkResult.Cancelled, a.Process(0, Cancel(0), out _));
        Assert.Equal(ArtworkResult.Ok, a.Process(0, Announce(0, 2, 1), out _));
        Assert.Equal(ArtworkResult.Completed, a.Process(0, Part(0, 7), out var image));
        Assert.Equal(new byte[] { 7 }, image!.Data);
    }

    [Fact]
    public void Cancel_without_a_transfer_is_harmless()
    {
        var a = new ArtworkAssembler();
        Assert.Equal(ArtworkResult.Cancelled, a.Process(2, Cancel(2), out _));
    }

    [Theory]
    [InlineData("announce_while_in_flight")]
    [InlineData("part_without_announce")]
    [InlineData("part_on_other_channel")]
    [InlineData("too_much_data")]
    [InlineData("reserved_flags")]
    [InlineData("short_announce")]
    [InlineData("cancel_with_payload")]
    [InlineData("empty")]
    public void Protocol_violations_are_reported(string scenario)
    {
        var a = new ArtworkAssembler();
        ArtworkResult result;
        switch (scenario)
        {
            case "announce_while_in_flight":
                a.Process(0, Announce(0, 1, 4), out _);
                result = a.Process(1, Announce(1, 1, 4), out _);
                break;
            case "part_without_announce":
                result = a.Process(0, Part(0, 1), out _);
                break;
            case "part_on_other_channel":
                a.Process(0, Announce(0, 1, 4), out _);
                result = a.Process(1, Part(1, 1), out _);
                break;
            case "too_much_data":
                a.Process(0, Announce(0, 1, 2), out _);
                result = a.Process(0, Part(0, 1, 2, 3), out _);
                break;
            case "reserved_flags":
                result = a.Process(0, [8, 0x10, 0], out _);
                break;
            case "short_announce":
                result = a.Process(0, [8, 2, 0, 0], out _);
                break;
            case "cancel_with_payload":
                result = a.Process(0, [8, 1, 9], out _);
                break;
            default:
                result = a.Process(0, [], out _);
                break;
        }

        Assert.Equal(ArtworkResult.ProtocolError, result);
    }

    [Fact]
    public void Oversized_images_are_counted_but_not_kept()
    {
        var a = new ArtworkAssembler();
        const uint total = 20 * 1024 * 1024;
        Assert.Equal(ArtworkResult.Ok, a.Process(0, Announce(0, 1, total), out _));
        Assert.Equal(ArtworkResult.Ok, a.Process(0, Part(0, 1, 2, 3), out var image));
        Assert.Null(image);
    }

    [Fact]
    public void Reset_drops_a_partial_transfer()
    {
        var a = new ArtworkAssembler();
        a.Process(0, Announce(0, 1, 4), out _);
        a.Reset();
        Assert.Equal(ArtworkResult.Ok, a.Process(0, Announce(0, 2, 1), out _));
    }
}
