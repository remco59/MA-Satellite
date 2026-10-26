using MaSatellite.Host.Autostart;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;

namespace MaSatellite.Windows.Autostart;

/// <summary>Autostart through the MSIX <c>windows.startupTask</c> extension declared in the manifest.</summary>
public sealed class PackagedAutostartService : IAutostartService
{
    /// <summary>Must match the TaskId of the startupTask extension in Package.appxmanifest.</summary>
    public const string TaskId = "MaSatelliteStartup";

    private readonly ILogger<PackagedAutostartService> _logger;

    public PackagedAutostartService(ILogger<PackagedAutostartService> logger)
    {
        _logger = logger;
    }

    public async Task<AutostartState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var task = await TryGetTaskAsync().ConfigureAwait(false);
        return task is null ? AutostartState.Unavailable : Map(task.State);
    }

    public async Task<AutostartState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var task = await TryGetTaskAsync().ConfigureAwait(false);
        if (task is null)
        {
            return AutostartState.Unavailable;
        }

        if (enabled)
        {
            var state = await task.RequestEnableAsync().AsTask(cancellationToken).ConfigureAwait(false);
            return Map(state);
        }

        task.Disable();
        return Map(task.State);
    }

    internal static AutostartState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => AutostartState.Enabled,
        StartupTaskState.Disabled => AutostartState.Disabled,
        StartupTaskState.DisabledByUser => AutostartState.DisabledByUser,
        StartupTaskState.DisabledByPolicy => AutostartState.DisabledByPolicy,
        _ => AutostartState.Unavailable,
    };

    private async Task<StartupTask?> TryGetTaskAsync()
    {
        try
        {
            return await StartupTask.GetAsync(TaskId).AsTask().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Not running with package identity (e.g. `dotnet run`) or the task id is not declared.
            _logger.LogDebug(ex, "Startup task {TaskId} is not available.", TaskId);
            return null;
        }
    }
}
