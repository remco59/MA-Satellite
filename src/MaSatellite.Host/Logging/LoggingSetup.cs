using MaSatellite.Host.Infrastructure;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace MaSatellite.Host.Logging;

public static class LoggingSetup
{
    /// <summary>Builds a logger factory writing rolling files to the logs folder and to the debug output.</summary>
    public static ILoggerFactory Create(AppPaths paths, bool verbose = false)
    {
        paths.EnsureCreated();
        var config = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "masatellite-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug();

        Log.Logger = config.CreateLogger();
        return new SerilogLoggerFactory(Log.Logger, dispose: true);
    }
}
