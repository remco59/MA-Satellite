using MaSatellite.Host.Autostart;
using MaSatellite.Windows.Autostart;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace MaSatellite.Windows.Tests;

public sealed class RegistryAutostartTests : IDisposable
{
    private readonly string _root = $@"Software\MaSatelliteTests\{Guid.NewGuid():N}";

    private string RunKey => _root + @"\Run";

    private string ApprovedKey => _root + @"\Approved";

    private RegistryAutostartService Create() =>
        new(@"C:\Apps\ma-satellite\MaSatellite.App.exe", NullLogger<RegistryAutostartService>.Instance, RunKey, ApprovedKey);

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    [Fact]
    public async Task Is_disabled_when_no_entry_exists()
    {
        Assert.Equal(AutostartState.Disabled, await Create().GetStateAsync());
    }

    [Fact]
    public async Task Enabling_writes_the_quoted_exe_path()
    {
        var state = await Create().SetEnabledAsync(true);

        Assert.Equal(AutostartState.Enabled, state);
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        Assert.Equal("\"C:\\Apps\\ma-satellite\\MaSatellite.App.exe\"", run?.GetValue(RegistryAutostartService.ValueName));
    }

    [Fact]
    public async Task Disabling_removes_the_entry()
    {
        var service = Create();
        await service.SetEnabledAsync(true);

        var state = await service.SetEnabledAsync(false);

        Assert.Equal(AutostartState.Disabled, state);
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        Assert.Null(run?.GetValue(RegistryAutostartService.ValueName));
    }

    [Fact]
    public async Task Task_manager_disabled_marker_is_reported_as_disabled_by_user()
    {
        var service = Create();
        await service.SetEnabledAsync(true);
        using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
        {
            approved.SetValue(RegistryAutostartService.ValueName, new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Assert.Equal(AutostartState.DisabledByUser, await service.GetStateAsync());
    }

    [Fact]
    public async Task Enabling_again_clears_the_disabled_marker()
    {
        var service = Create();
        await service.SetEnabledAsync(true);
        using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
        {
            approved.SetValue(RegistryAutostartService.ValueName, new byte[] { 0x03, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Assert.Equal(AutostartState.Enabled, await service.SetEnabledAsync(true));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, true)]
    [InlineData(new byte[0], false)]
    public void Disabled_marker_is_the_low_bit_of_the_first_byte(byte[] data, bool expected)
    {
        Assert.Equal(expected, RegistryAutostartService.IsDisabledMarker(data));
    }

    [Fact]
    public void Missing_marker_is_not_disabled()
    {
        Assert.False(RegistryAutostartService.IsDisabledMarker(null));
    }
}
