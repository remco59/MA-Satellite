using System.Runtime.InteropServices;

namespace MaSatellite.Windows.Autostart;

/// <summary>Tells whether the process runs with MSIX package identity (store/sideloaded package) or as a plain installed exe.</summary>
public static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) == ErrorInsufficientBuffer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
