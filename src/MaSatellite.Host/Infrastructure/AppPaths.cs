namespace MaSatellite.Host.Infrastructure;

/// <summary>Resolves the on-disk locations used by the app. A root override keeps tests off the real profile.</summary>
public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaSatellite");
    }

    public string Root { get; }

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string SecretsDirectory => Path.Combine(Root, "secrets");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
