using MaSatellite.Host.Autostart;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace MaSatellite.Windows.Autostart;

/// <summary>Autostart for the installer (unpackaged) build: a value in the per-user <c>Run</c> key, honouring Task Manager's "Startup apps" switch.</summary>
public sealed class RegistryAutostartService : IAutostartService
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "MaSatellite";

    private readonly string _exePath;
    private readonly string _runKey;
    private readonly string _approvedKey;
    private readonly ILogger<RegistryAutostartService> _logger;

    public RegistryAutostartService(
        string exePath,
        ILogger<RegistryAutostartService> logger,
        string runKey = DefaultRunKey,
        string approvedKey = DefaultApprovedKey)
    {
        _exePath = exePath;
        _logger = logger;
        _runKey = runKey;
        _approvedKey = approvedKey;
    }

    public Task<AutostartState> GetStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ReadState());

    public Task<AutostartState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            if (enabled)
            {
                using var run = Registry.CurrentUser.CreateSubKey(_runKey, writable: true);
                run.SetValue(ValueName, Quote(_exePath), RegistryValueKind.String);

                // Re-enabling from inside the app also clears an old "disabled" mark left by Task Manager.
                using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                using var run = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
                run?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Could not update the autostart registry entry.");
            return Task.FromResult(AutostartState.Unavailable);
        }

        return Task.FromResult(ReadState());
    }

    /// <summary>Task Manager marks a disabled startup entry with an odd first byte (0x03); 0x02/0x06 mean enabled.</summary>
    internal static bool IsDisabledMarker(byte[]? data) => data is { Length: > 0 } && (data[0] & 1) == 1;

    private static string Quote(string path) => $"\"{path}\"";

    private AutostartState ReadState()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(_runKey);
            if (run?.GetValue(ValueName) is not string)
            {
                return AutostartState.Disabled;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey);
            return IsDisabledMarker(approved?.GetValue(ValueName) as byte[])
                ? AutostartState.DisabledByUser
                : AutostartState.Enabled;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the autostart registry entry.");
            return AutostartState.Unavailable;
        }
    }
}
