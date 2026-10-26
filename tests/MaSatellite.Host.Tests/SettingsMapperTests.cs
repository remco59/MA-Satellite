using MaSatellite.Host.Orchestration;
using MaSatellite.Host.Settings;
using CoreMode = MaSatellite.Core.Session.ConnectionMode;

namespace MaSatellite.Host.Tests;

public class SettingsMapperTests
{
    private static readonly AppSettings Base = new() { ClientId = "11111111-1111-1111-1111-111111111111", PlayerName = "PC", MaHost = "192.168.0.98" };

    [Fact]
    public void Maps_options()
    {
        var o = SettingsMapper.ToSendspinOptions(Base with { StaticDelayMs = 40 }, "1.2.3");
        Assert.Equal("PC", o.PlayerName);
        Assert.Equal(40, o.StaticDelayMs);
        Assert.Equal("1.2.3", o.SoftwareVersion);

        var c = SettingsMapper.ToConnectionOptions(Base with { ConnectionMode = ConnectionMode.Connect, ServerPort = 9000 });
        Assert.Equal(CoreMode.Connect, c.Mode);
        Assert.Equal("192.168.0.98", c.ServerHost);
        Assert.Equal(9000, c.ServerPort);
        Assert.Equal(CoreMode.Advertise, SettingsMapper.ToConnectionOptions(Base).Mode);
    }

    [Theory]
    [InlineData("PlayerName", true)]
    [InlineData("ListenPort", true)]
    [InlineData("ConnectionMode", true)]
    [InlineData("StaticDelayMs", false)]
    [InlineData("OutputDeviceId", false)]
    [InlineData("Autostart", false)]
    [InlineData("MaHostAdvertise", false)] // host is irrelevant while advertising
    public void Reconnect_only_when_connection_relevant_settings_change(string field, bool expected)
    {
        var after = field switch
        {
            "PlayerName" => Base with { PlayerName = "Other" },
            "ListenPort" => Base with { ListenPort = 9999 },
            "ConnectionMode" => Base with { ConnectionMode = ConnectionMode.Connect },
            "StaticDelayMs" => Base with { StaticDelayMs = 10 },
            "OutputDeviceId" => Base with { OutputDeviceId = "x" },
            "Autostart" => Base with { Autostart = false },
            _ => Base with { MaHost = "other.local" },
        };

        Assert.Equal(expected, SettingsMapper.RequiresReconnect(Base, after));
    }

    [Fact]
    public void Host_change_matters_in_connect_mode()
    {
        var connect = Base with { ConnectionMode = ConnectionMode.Connect };
        Assert.True(SettingsMapper.RequiresReconnect(connect, connect with { MaHost = "other" }));
    }

    [Fact]
    public void Last_server_store_persists()
    {
        using var dir = new TempDir();
        var paths = new MaSatellite.Host.Infrastructure.AppPaths(dir.Path);
        var a = new FileLastServerStore(paths) { LastPlayedServerId = "srv-1" };
        Assert.Equal("srv-1", new FileLastServerStore(paths).LastPlayedServerId);
        a.LastPlayedServerId = null;
        Assert.Null(new FileLastServerStore(paths).LastPlayedServerId);
    }
}
