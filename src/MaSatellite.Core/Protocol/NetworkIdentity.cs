using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MaSatellite.Core.Protocol;

/// <summary>Stable hardware identity reported to Music Assistant for Home Assistant device matching.</summary>
public static class NetworkIdentity
{
    public static string GetPrimaryMacAddress()
    {
        try
        {
            var candidate = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .Select(n => new
                {
                    Interface = n,
                    Address = n.GetPhysicalAddress().GetAddressBytes(),
                    HasIpv4 = n.GetIPProperties().UnicastAddresses.Any(a =>
                        a.Address.AddressFamily == AddressFamily.InterNetwork
                        && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)),
                })
                .Where(x => x.HasIpv4 && x.Address.Length == 6 && x.Address.Any(b => b != 0))
                .OrderByDescending(x => x.Interface.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .FirstOrDefault();

            return candidate is null
                ? string.Empty
                : string.Join(":", candidate.Address.Select(b => b.ToString("x2")));
        }
        catch (NetworkInformationException)
        {
            return string.Empty;
        }
    }
}
