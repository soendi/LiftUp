using System.Globalization;
using LiftUp.Core.Data;

namespace LiftUp.Core.Sync;

/// <summary>
/// Ablage der Server-Zugangsdaten und des Abgleichsstands. Die Werte stehen in
/// der Tabelle <c>Settings</c> der lokalen Datenbank, das Passwort geschützt.
/// </summary>
public sealed class SyncSettingsStore(Database database)
{
    private const string HostKey = "Sync.Host";
    private const string PortKey = "Sync.Port";
    private const string DatabaseKey = "Sync.Database";
    private const string UserKey = "Sync.User";
    private const string PasswordKey = "Sync.Password";
    private const string SslModeKey = "Sync.SslMode";
    private const string LastChangeIdKey = "Sync.LastChangeId";
    private const string LastRunUtcKey = "Sync.LastRunUtc";
    private const string LastRunOkKey = "Sync.LastRunOk";
    private const string LastMessageKey = "Sync.LastMessage";

    public DatabaseServerSettings? Load()
    {
        var host = database.GetSetting(HostKey);
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var port = int.TryParse(database.GetSetting(PortKey), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsedPort)
            ? parsedPort
            : DatabaseServerSettings.DefaultPort;

        return new DatabaseServerSettings
        {
            Host = host,
            Port = port,
            Database = database.GetSetting(DatabaseKey) ?? DatabaseServerSettings.DefaultDatabase,
            User = database.GetSetting(UserKey) ?? DatabaseServerSettings.DefaultUser,
            Password = LocalSecretProtector.Unprotect(database.GetSetting(PasswordKey)),
            SslMode = database.GetSetting(SslModeKey) ?? "Prefer",
        };
    }

    public void Save(DatabaseServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        database.SetSetting(HostKey, settings.Host.Trim());
        database.SetSetting(PortKey, settings.Port.ToString(CultureInfo.InvariantCulture));
        database.SetSetting(DatabaseKey, settings.Database.Trim());
        database.SetSetting(UserKey, settings.User.Trim());
        database.SetSetting(PasswordKey, LocalSecretProtector.Protect(settings.Password));
        database.SetSetting(SslModeKey, settings.SslMode);
    }

    public void Clear()
    {
        foreach (var key in new[]
                 {
                     HostKey, PortKey, DatabaseKey, UserKey, PasswordKey, SslModeKey,
                     LastChangeIdKey, LastRunUtcKey, LastRunOkKey, LastMessageKey,
                 })
        {
            database.SetSetting(key, string.Empty);
        }
    }

    /// <summary>Zuletzt gelesene Nummer im Änderungsprotokoll des Servers.</summary>
    public long LastChangeId =>
        long.TryParse(database.GetSetting(LastChangeIdKey), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;

    public void SetLastChangeId(long value) =>
        database.SetSetting(LastChangeIdKey, value.ToString(CultureInfo.InvariantCulture));

    public DateTime? LastRunUtc =>
        DateTime.TryParse(database.GetSetting(LastRunUtcKey), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value)
            ? value.ToUniversalTime()
            : null;

    public bool? LastRunSucceeded =>
        bool.TryParse(database.GetSetting(LastRunOkKey), out var value) ? value : null;

    public string LastMessage => database.GetSetting(LastMessageKey) ?? string.Empty;

    public void SetLastResult(DateTime utc, bool succeeded, string message)
    {
        database.SetSetting(LastRunUtcKey, utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        database.SetSetting(LastRunOkKey, succeeded ? "true" : "false");
        database.SetSetting(LastMessageKey, message);
    }
}
