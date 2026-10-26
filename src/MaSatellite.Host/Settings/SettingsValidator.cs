namespace MaSatellite.Host.Settings;

public static class SettingsValidator
{
    public const int MaxPlayerNameLength = 64;

    /// <summary>Returns human readable problems; empty means valid.</summary>
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.PlayerName))
        {
            errors.Add("Player name must not be empty.");
        }
        else if (settings.PlayerName.Length > MaxPlayerNameLength)
        {
            errors.Add($"Player name must be at most {MaxPlayerNameLength} characters.");
        }

        if (!Guid.TryParse(settings.ClientId, out _))
        {
            errors.Add("Client id must be a GUID.");
        }

        ValidatePort(errors, settings.ListenPort, "Listen port");
        ValidatePort(errors, settings.ServerPort, "Server port");
        ValidatePort(errors, settings.MaWebPort, "Music Assistant web port");
        ValidatePort(errors, settings.LocalApiPort, "Local API port");

        if (settings.StaticDelayMs is < 0 or > 5000)
        {
            errors.Add("Static delay must be between 0 and 5000 ms.");
        }

        if (settings.ConnectionMode == ConnectionMode.Connect && string.IsNullOrWhiteSpace(settings.MaHost))
        {
            errors.Add("Music Assistant host is required when connecting to a server.");
        }

        if (!string.IsNullOrWhiteSpace(settings.MaHost) && !IsPlausibleHost(settings.MaHost))
        {
            errors.Add("Music Assistant host must be a host name or IP address without scheme or port.");
        }

        return errors;
    }

    private static void ValidatePort(List<string> errors, int port, string label)
    {
        if (port is < 1 or > 65535)
        {
            errors.Add($"{label} must be between 1 and 65535.");
        }
    }

    private static bool IsPlausibleHost(string host)
    {
        host = host.Trim();
        if (host.Length == 0 || host.Contains("://", StringComparison.Ordinal) || host.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        if (System.Net.IPAddress.TryParse(host, out _))
        {
            return true;
        }

        return Uri.CheckHostName(host) is UriHostNameType.Dns;
    }
}
