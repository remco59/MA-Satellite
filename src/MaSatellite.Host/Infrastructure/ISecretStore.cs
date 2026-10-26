namespace MaSatellite.Host.Infrastructure;

/// <summary>Stores secrets (the Music Assistant token) outside the plain-text settings file. Values are never logged.</summary>
public interface ISecretStore
{
    string? Get(string name);

    void Set(string name, string value);

    void Delete(string name);
}

public static class SecretNames
{
    public const string MaToken = "ma-token";

    /// <summary>Static key, pairing PSK and paired-server PSKs of the encrypted Sendspin protocol.</summary>
    public const string SendspinKeys = "sendspin-keys";
}

/// <summary>Non-persistent store for tests and unsupported platforms.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public void Set(string name, string value) => _values[name] = value;

    public void Delete(string name) => _values.Remove(name);
}
