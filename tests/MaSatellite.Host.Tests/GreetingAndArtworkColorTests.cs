using MaSatellite.Host.Presentation;

namespace MaSatellite.Host.Tests;

public class GreetingTests
{
    [Theory]
    [InlineData(6, "Good morning, Remco")]
    [InlineData(14, "Good afternoon, Remco")]
    [InlineData(20, "Good evening, Remco")]
    [InlineData(2, "Good night, Remco")]
    public void Greets_by_time_of_day(int hour, string expected) =>
        Assert.Equal(expected, Greeting.For(new DateTime(2026, 1, 1, hour, 0, 0), "REMCO"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin.user")]
    [InlineData("user123")]
    public void Falls_back_to_a_neutral_greeting_without_a_plain_name(string? name) =>
        Assert.Equal("Good afternoon", Greeting.For(new DateTime(2026, 1, 1, 14, 0, 0), name));

    [Fact]
    public void Part_of_day_matches_the_greeting() => Assert.Equal("afternoon", Greeting.PartOfDay(13));
}

public class ArtworkColorTests
{
    private static byte[] Solid(byte r, byte g, byte b, int pixels = 16) =>
        Enumerable.Range(0, pixels).SelectMany(_ => new byte[] { b, g, r, 255 }).ToArray();

    [Fact]
    public void Picks_the_dominant_saturated_colour()
    {
        var c = ArtworkColor.Pick(Solid(200, 40, 40));
        Assert.Equal(new Rgb(200, 40, 40), c);
    }

    [Fact]
    public void Saturated_pixels_outweigh_grey_ones()
    {
        var data = Solid(120, 120, 120, 12).Concat(Solid(30, 90, 220, 4)).ToArray();
        var c = ArtworkColor.Pick(data);
        Assert.True(c.B > c.R + 40);
    }

    [Fact]
    public void Black_white_and_transparent_pixels_fall_back()
    {
        var data = Solid(0, 0, 0, 4).Concat(Solid(255, 255, 255, 4))
            .Concat(new byte[] { 10, 200, 10, 0 }).ToArray();
        Assert.Equal(Rgb.Fallback, ArtworkColor.Pick(data));
    }

    [Fact]
    public void Backdrop_is_dark_enough_for_light_text()
    {
        var b = new Rgb(250, 240, 20).ToBackdrop();
        Assert.True(Math.Max(b.R, Math.Max(b.G, b.B)) <= 0.34 * 255 + 1);
    }

    [Fact]
    public void Backdrop_never_collapses_to_pure_black() =>
        Assert.NotEqual(new Rgb(0, 0, 0), new Rgb(0, 0, 0).ToBackdrop());
}
