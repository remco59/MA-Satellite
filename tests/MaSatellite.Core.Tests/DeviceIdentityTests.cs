using System.Text.Json;
using MaSatellite.Core.Protocol;

namespace MaSatellite.Core.Tests;

public sealed class DeviceIdentityTests
{
    [Fact]
    public void Client_hello_serializes_mac_address_using_sendspin_wire_name()
    {
        const string mac = "12:34:56:78:9A:BC";
        var hello = new ClientHello(
            "client-id",
            "Desk",
            new DeviceInfo("ma-satellite", "ma-satellite", "1.0.0", mac),
            1,
            [Roles.Player],
            new PlayerSupport(Array.Empty<AudioFormatSpec>(), 4096, Array.Empty<string>()),
            null);

        using var document = JsonDocument.Parse(MessageCodec.ClientHello(hello));
        var deviceInfo = document.RootElement
            .GetProperty("payload")
            .GetProperty("device_info");

        Assert.Equal(mac, deviceInfo.GetProperty("mac_address").GetString());
    }
}
