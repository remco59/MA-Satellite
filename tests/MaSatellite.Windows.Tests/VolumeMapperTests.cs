using MaSatellite.Windows.Audio;

namespace MaSatellite.Windows.Tests;

public class VolumeMapperTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 100)]
    [InlineData(0.33f, 33)]
    [InlineData(0.335f, 34)]
    [InlineData(0.504f, 50)]
    [InlineData(-1f, 0)]
    [InlineData(2f, 100)]
    public void Scalar_to_percent(float scalar, int expected) => Assert.Equal(expected, VolumeMapper.ToPercent(scalar));

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(100, 1f)]
    [InlineData(50, 0.5f)]
    [InlineData(150, 1f)]
    [InlineData(-5, 0f)]
    public void Percent_to_scalar(int percent, float expected) => Assert.Equal(expected, VolumeMapper.ToScalar(percent));

    [Fact]
    public void Round_trip_is_stable_for_every_percent()
    {
        for (var p = 0; p <= 100; p++)
        {
            Assert.Equal(p, VolumeMapper.ToPercent(VolumeMapper.ToScalar(p)));
        }
    }
}
