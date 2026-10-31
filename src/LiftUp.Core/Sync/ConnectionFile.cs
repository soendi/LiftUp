using System.Text.Json;
using System.Text.Json.Serialization;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Sync;

/// <summary>
/// Die Datei, die das Server-Setup nach dem Einrichten der Datenbank ablegt
/// (standardmäßig auf dem Desktop). Der Client liest sie über den Reiter
/// „Datenbank“ ein, damit niemand Host, Benutzer und Passwort abtippen muss.
/// </summary>
public sealed class ConnectionFile
{
    public const string DefaultFileName = "LiftUp-Datenbank-Zugang.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonPropertyName("produkt")]
    public string Product { get; set; } = "LiftUp";

    [JsonPropertyName("art")]
    public string Kind { get; set; } = "Server-Datenbank (PostgreSQL)";

    [JsonPropertyName("erstelltUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("server")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = DatabaseServerSettings.DefaultPort;

    [JsonPropertyName("datenbank")]
    public string Database { get; set; } = DatabaseServerSettings.DefaultDatabase;

    [JsonPropertyName("benutzer")]
    public string User { get; set; } = DatabaseServerSettings.DefaultUser;

    [JsonPropertyName("passwort")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("sslmode")]
    public string SslMode { get; set; } = "Prefer";

    [JsonPropertyName("postgresql")]
    public string ServerVersion { get; set; } = string.Empty;

    [JsonPropertyName("hinweis")]
    public string Hint { get; set; } =
        "Diese Datei enthält das Passwort der LiftUp-Datenbank. Im Reiter „Datenbank“ einlesen und danach sicher aufbewahren.";

    public static ConnectionFile From(DatabaseServerSettings settings, string? serverVersion = null) =>
        new()
        {
            Host = settings.Host,
            Port = settings.Port,
            Database = settings.Database,
            User = settings.User,
            Password = settings.Password,
            SslMode = settings.SslMode,
            ServerVersion = serverVersion ?? string.Empty,
        };

    public DatabaseServerSettings ToSettings() =>
        new()
        {
            Host = Host,
            Port = Port,
            Database = Database,
            User = User,
            Password = Password,
            SslMode = string.IsNullOrWhiteSpace(SslMode) ? "Prefer" : SslMode,
        };

    public void Save(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions) + Environment.NewLine);
    }

    /// <summary>Liest die Datei und prüft, dass sie zu LiftUp gehört.</summary>
    public static ConnectionFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ValidationException($"Die Datei „{path}“ wurde nicht gefunden.");
        }

        ConnectionFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ConnectionFile>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            throw new ValidationException(
                "Die Datei ist keine gültige LiftUp-Verbindungsdatei (JSON konnte nicht gelesen werden).");
        }

        if (file is null || string.IsNullOrWhiteSpace(file.Host) || string.IsNullOrWhiteSpace(file.User))
        {
            throw new ValidationException(
                "Die Datei enthält keine Zugangsdaten. Bitte die Datei wählen, die das LiftUp-Server-Setup erzeugt hat.");
        }

        var errors = file.ToSettings().Validate();
        if (errors.Count > 0)
        {
            throw new ValidationException(string.Join(" ", errors));
        }

        return file;
    }

    /// <summary>
    /// Vorschlag für den Ablageort: Desktop des angemeldeten Benutzers,
    /// ersatzweise der Dokumente-Ordner und zuletzt das Benutzerprofil.
    /// </summary>
    public static string SuggestPath()
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                 })
        {
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                return Path.Combine(folder, DefaultFileName);
            }
        }

        return Path.Combine(Path.GetTempPath(), DefaultFileName);
    }
}
