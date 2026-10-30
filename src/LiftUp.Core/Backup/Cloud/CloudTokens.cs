using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Backup.Cloud;

/// <summary>Zugangsdaten einer angemeldeten Cloud-Verbindung.</summary>
public sealed class CloudTokenSet
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string AccountName { get; set; } = string.Empty;

    public bool IsExpired => DateTime.UtcNow >= ExpiresUtc.AddMinutes(-2);
}

/// <summary>
/// Legt Tokens dateiweise ab – mit DPAPI an den angemeldeten Windows-Benutzer gebunden.
/// Damit kann kein anderer Benutzer des Rechners die Anmeldung übernehmen.
/// </summary>
public sealed class CloudTokenStore
{
    private readonly string _folder;

    public CloudTokenStore(string folder) => _folder = folder;

    public CloudTokenSet? Load(BackupTargetKind kind)
    {
        var path = GetPath(kind);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var plain = Unprotect(encrypted);
            return JsonSerializer.Deserialize<CloudTokenSet>(plain);
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Save(BackupTargetKind kind, CloudTokenSet tokens)
    {
        Directory.CreateDirectory(_folder);
        var json = JsonSerializer.SerializeToUtf8Bytes(tokens);
        File.WriteAllBytes(GetPath(kind), Protect(json));
    }

    public void Delete(BackupTargetKind kind)
    {
        var path = GetPath(kind);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public string GetPath(BackupTargetKind kind) =>
        Path.Combine(_folder, $"{kind.ToString().ToLowerInvariant()}.token");

    private static byte[] Protect(byte[] data)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ValidationException("Die Cloud-Anmeldung ist nur unter Windows verfügbar.");
        }

        return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    }

    private static string Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ValidationException("Die Cloud-Anmeldung ist nur unter Windows verfügbar.");
        }

        var plain = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
