using MaSatellite.Host.Autostart;
using Microsoft.Extensions.Logging;

namespace MaSatellite.Windows.Autostart;

public static class AutostartServiceFactory
{
    /// <summary>MSIX builds use the manifest's startup task; the installer (unpackaged) build uses the per-user Run key.</summary>
    public static IAutostartService Create(ILoggerFactory loggers, string exePath) =>
        PackageIdentity.IsPackaged
            ? new PackagedAutostartService(loggers.CreateLogger<PackagedAutostartService>())
            : new RegistryAutostartService(exePath, loggers.CreateLogger<RegistryAutostartService>());
}
