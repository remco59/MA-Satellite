using MaSatellite.Core.Session;
using MaSatellite.Host.Settings;
using CoreConnectionMode = MaSatellite.Core.Session.ConnectionMode;
using SettingsConnectionMode = MaSatellite.Host.Settings.ConnectionMode;

namespace MaSatellite.Host.Orchestration;

public static class SettingsMapper
{
    public static SendspinOptions ToSendspinOptions(AppSettings s, string softwareVersion) => new()
    {
        ClientId = s.ClientId,
        PlayerName = s.PlayerName,
        SoftwareVersion = softwareVersion,
        StaticDelayMs = s.StaticDelayMs,
    };

    public static ServerConnectionOptions ToConnectionOptions(AppSettings s) => new()
    {
        Mode = s.ConnectionMode == SettingsConnectionMode.Connect ? CoreConnectionMode.Connect : CoreConnectionMode.Advertise,
        ListenPort = s.ListenPort,
        ServerHost = s.MaHost,
        ServerPort = s.ServerPort,
    };

    /// <summary>True when the change needs the connection to be re-established (identity, ports, mode, host).</summary>
    public static bool RequiresReconnect(AppSettings before, AppSettings after) =>
        before.ClientId != after.ClientId
        || before.PlayerName != after.PlayerName
        || before.ConnectionMode != after.ConnectionMode
        || before.ListenPort != after.ListenPort
        || (after.ConnectionMode == SettingsConnectionMode.Connect && (before.MaHost != after.MaHost || before.ServerPort != after.ServerPort));
}
