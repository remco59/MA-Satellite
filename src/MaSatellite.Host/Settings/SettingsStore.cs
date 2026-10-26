using System.Text.Json;
using MaSatellite.Host.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MaSatellite.Host.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Never throws on a bad file: it falls back to defaults.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly AppPaths _paths;
    private readonly ILogger<SettingsStore> _logger;
    private readonly object _gate = new();

    public SettingsStore(AppPaths paths, ILogger<SettingsStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Load()
    {
        lock (_gate)
        {
            _paths.EnsureCreated();
            AppSettings loaded;
            if (!File.Exists(_paths.SettingsFile))
            {
                loaded = new AppSettings();
                WriteFile(loaded);
                _logger.LogInformation("Created default settings at {Path}", _paths.SettingsFile);
            }
            else
            {
                loaded = TryRead() ?? new AppSettings();
            }

            Current = loaded;
            return loaded;
        }
    }

    /// <summary>Validates, persists and publishes the new settings. Returns validation errors without saving when invalid.</summary>
    public IReadOnlyList<string> Save(AppSettings settings)
    {
        var errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0)
        {
            return errors;
        }

        lock (_gate)
        {
            _paths.EnsureCreated();
            WriteFile(settings);
            Current = settings;
        }

        Changed?.Invoke(this, settings);
        return Array.Empty<string>();
    }

    private AppSettings? TryRead()
    {
        try
        {
            var json = File.ReadAllText(_paths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                throw new JsonException("Settings file was empty.");
            }

            var errors = SettingsValidator.Validate(settings);
            if (errors.Count > 0)
            {
                _logger.LogWarning("Settings file has invalid values ({Errors}); keeping a backup and using defaults.", string.Join(" ", errors));
                Backup();
                return keepClientId(settings);
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read settings; keeping a backup and using defaults.");
            Backup();
            return null;
        }

        static AppSettings keepClientId(AppSettings s) => new() { ClientId = Guid.TryParse(s.ClientId, out _) ? s.ClientId : Guid.NewGuid().ToString("D") };
    }

    private void Backup()
    {
        try
        {
            var backup = _paths.SettingsFile + ".bad";
            File.Copy(_paths.SettingsFile, backup, overwrite: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private void WriteFile(AppSettings settings)
    {
        var tmp = _paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, _paths.SettingsFile, overwrite: true);
    }
}
