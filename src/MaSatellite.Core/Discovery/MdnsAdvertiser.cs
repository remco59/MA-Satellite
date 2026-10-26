using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Core.Discovery;

/// <summary>Advertises <c>_sendspin._tcp.local.</c> with TXT <c>path</c> and <c>name</c>, as the Sendspin spec requires.</summary>
public sealed class MdnsAdvertiser : IServiceAdvertiser
{
    public const string ServiceType = "_sendspin._tcp";

    private readonly ILogger _logger;

    public MdnsAdvertiser(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public IDisposable Advertise(string instanceName, int port, string path, string friendlyName)
    {
        var addresses = UsableAddresses();
        var profile = new ServiceProfile(SanitizeLabel(instanceName), ServiceType, (ushort)port, addresses);
        profile.AddProperty("path", path);
        profile.AddProperty("name", friendlyName);

        var discovery = new ServiceDiscovery();
        discovery.Advertise(profile);
        discovery.Announce(profile);
        _logger.LogInformation("Advertising {Service} as '{Instance}' on port {Port} ({Count} addresses)", ServiceType, instanceName, port, addresses.Count);
        return new Handle(discovery, profile);
    }

    /// <summary>Non-loopback, non-link-local addresses of interfaces that are up (servers must not connect to loopback).</summary>
    internal static List<IPAddress> UsableAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = unicast.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip))
                {
                    continue;
                }

                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254)
                {
                    continue; // APIPA
                }

                result.Add(ip);
            }
        }

        return result;
    }

    internal static string SanitizeLabel(string name)
    {
        var cleaned = new string(name.Select(c => c == '.' ? '-' : c).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "ma-satellite";
        }

        return cleaned.Length > 63 ? cleaned[..63] : cleaned;
    }

    private sealed class Handle : IDisposable
    {
        private readonly ServiceDiscovery _discovery;
        private readonly ServiceProfile _profile;

        public Handle(ServiceDiscovery discovery, ServiceProfile profile)
        {
            _discovery = discovery;
            _profile = profile;
        }

        public void Dispose()
        {
            try
            {
                _discovery.Unadvertise(_profile);
            }
            catch (Exception)
            {
                // best effort goodbye packet
            }

            _discovery.Dispose();
        }
    }
}
