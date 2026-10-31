using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Data;

/// <summary>
/// Merkt sich die eingelesene Verbindungsdatei in der lokalen Datenbank, damit sie
/// nach einem Neustart nicht erneut gewählt werden muss. Die Angaben liegen in
/// <c>Settings</c>; das Passwort steht getrennt davon und ist unter Windows über
/// DPAPI an den angemeldeten Benutzer gebunden.
/// </summary>
public sealed class ServerProfileStore(Database database)
{
    private const string ProfileKey = "ServerProfile";
    private const string PasswordKey = "ServerPassword";
    private const string SourcePathKey = "ServerProfileSourcePath";

    /// <summary>Gespeicherte Angaben oder <c>null</c>, wenn noch keine Datei eingelesen wurde.</summary>
    public ServerProfile? Load()
    {
        var json = database.GetSetting(ProfileKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        ServerProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<ServerProfile>(json, ServerProfile.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (profile is null)
        {
            return null;
        }

        profile.Password = Unprotect(database.GetSetting(PasswordKey) ?? string.Empty);
        return profile;
    }

    /// <summary>Pfad der zuletzt eingelesenen Datei, nur für die Anzeige.</summary>
    public string? GetSourcePath()
    {
        var path = database.GetSetting(SourcePathKey);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public void Save(ServerProfile profile, string? sourcePath = null)
    {
        profile.Validate();

        // Das Passwort gehört nicht in die Einstellungen mit den übrigen Angaben.
        var stored = JsonSerializer.Deserialize<ServerProfile>(
            JsonSerializer.Serialize(profile, ServerProfile.JsonOptions), ServerProfile.JsonOptions)!;
        stored.Password = string.Empty;

        database.SetSetting(ProfileKey, JsonSerializer.Serialize(stored, ServerProfile.JsonOptions));
        database.SetSetting(PasswordKey, Protect(profile.Password));

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            database.SetSetting(SourcePathKey, Path.GetFullPath(sourcePath));
        }
    }

    public void Clear()
    {
        database.SetSetting(ProfileKey, string.Empty);
        database.SetSetting(PasswordKey, string.Empty);
        database.SetSetting(SourcePathKey, string.Empty);
    }

    private static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret) || !OperatingSystem.IsWindows())
        {
            // Ohne DPAPI (Tests, Linux-Build) bleibt der Wert lesbar; die Datei selbst
            // ist dann nicht besser geschützt als die lokale Datenbank.
            return secret;
        }

        try
        {
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }

    private static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !OperatingSystem.IsWindows())
        {
            return stored;
        }

        try
        {
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            // Anderer Windows-Benutzer oder andere Datenbank: Passwort erneut einlesen.
            return string.Empty;
        }
    }
}
