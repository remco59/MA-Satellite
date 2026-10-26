using MaSatellite.Host.Autostart;
using MaSatellite.Windows.Autostart;
using Windows.ApplicationModel;

namespace MaSatellite.Windows.Tests;

public class AutostartMappingTests
{
    [Theory]
    [InlineData(StartupTaskState.Enabled, AutostartState.Enabled)]
    [InlineData(StartupTaskState.EnabledByPolicy, AutostartState.Enabled)]
    [InlineData(StartupTaskState.Disabled, AutostartState.Disabled)]
    [InlineData(StartupTaskState.DisabledByUser, AutostartState.DisabledByUser)]
    [InlineData(StartupTaskState.DisabledByPolicy, AutostartState.DisabledByPolicy)]
    public void Maps_startup_task_states(StartupTaskState input, AutostartState expected)
    {
        Assert.Equal(expected, PackagedAutostartService.Map(input));
    }
}
