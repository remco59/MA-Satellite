using System.Security.Cryptography;
using System.Text;
using MaSatellite.Host.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Windows.Security;

/// <summary>Secrets encrypted with Windows DPAPI (current user) in the app data folder. Only this Windows user can decrypt them.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MaSatellite.secrets.v1");

    private readonly string _directory;
    private readonly ILogger _logger;

    public DpapiSecretStore(AppPaths paths, ILogger? logger = null)
    {
        _directory = paths.SecretsDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    public string? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            // Never include the content; a different Windows user or a corrupted file simply yields "no secret".
            _logger.LogWarning("Secret '{Name}' could not be read ({Reason}); treating it as missing", name, ex.GetType().Name);
            return null;
        }
    }

    public void Set(string name, string value)
    {
        Directory.CreateDirectory(_directory);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        var path = PathFor(name);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string name)
    {
        if (name.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
        {
            throw new ArgumentException("Secret names may only contain letters, digits, '-' and '_'.", nameof(name));
        }

        return Path.Combine(_directory, name + ".bin");
    }
}
