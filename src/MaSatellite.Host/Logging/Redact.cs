namespace MaSatellite.Host.Logging;

/// <summary>Helpers so secrets (MA tokens) never end up in logs.</summary>
public static class Redact
{
    public static string Secret(string? value) =>
        string.IsNullOrEmpty(value) ? "<empty>" : $"<redacted:{value.Length} chars>";
}
