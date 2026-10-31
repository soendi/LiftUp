using System.Security.Cryptography;
using System.Text;

namespace LiftUp.Core.Sync;

/// <summary>
/// Schützt das Datenbankpasswort in der lokalen SQLite-Datei. Unter Windows
/// geschieht das über DPAPI und ist damit an den angemeldeten Benutzer gebunden;
/// auf anderen Systemen (Tests, Entwicklung) wird Base64 verwendet.
/// </summary>
internal static class LocalSecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string Base64Prefix = "base64:";

    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (OperatingSystem.IsWindows())
        {
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(protectedBytes);
        }

        return Base64Prefix + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return string.Empty;
        }

        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                {
                    return string.Empty;
                }

                var data = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                var plain = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }

            if (stored.StartsWith(Base64Prefix, StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[Base64Prefix.Length..]));
            }

            // Ältere/unkodierte Werte bleiben lesbar.
            return stored;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
}
