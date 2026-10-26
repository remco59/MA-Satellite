using MaSatellite.Windows.Media;
using Windows.Media;

namespace MaSatellite.Windows.Tests;

public class SmtcMappingTests
{
    [Theory]
    [InlineData(SystemMediaTransportControlsButton.Play, "play")]
    [InlineData(SystemMediaTransportControlsButton.Pause, "pause")]
    [InlineData(SystemMediaTransportControlsButton.Stop, "stop")]
    [InlineData(SystemMediaTransportControlsButton.Next, "next")]
    [InlineData(SystemMediaTransportControlsButton.Previous, "previous")]
    public void Buttons_map_to_controller_commands(SystemMediaTransportControlsButton button, string expected)
    {
        Assert.Equal(expected, SmtcController.Map(button)?.Command);
    }

    [Fact]
    public void Unsupported_buttons_are_ignored()
    {
        Assert.Null(SmtcController.Map(SystemMediaTransportControlsButton.Record));
        Assert.Null(SmtcController.Map(SystemMediaTransportControlsButton.FastForward));
    }
}
