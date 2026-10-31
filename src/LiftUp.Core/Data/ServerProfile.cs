using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LiftUp.Core.Domain;
using Npgsql;

namespace LiftUp.Core.Data;

/// <summary>
/// Verbindungsdaten der zentralen PostgreSQL-Datenbank. Die Datei entsteht beim
/// Aufruf von <c>installer/Install-ServerDatabase.ps1</c> auf dem Datenbank-Server
/// und wird im Reiter „Datenbank“ eingelesen.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed class ServerProfile
{
    public const string FormatName = "liftup-server";
    public const int CurrentFormatVersion = 1;
    public const int DefaultPort = 5432;

    public static readonly string[] AllowedSslModes =
        ["Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull"];

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly Regex SchemaNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Kennung des Dateiformats, damit keine beliebige JSON-Datei eingelesen wird.</summary>
    public string Format { get; set; } = string.Empty;

    public int FormatVersion { get; set; }

    public DateTime GeneratedUtc { get; set; }

    public string? GeneratedBy { get; set; }

    public string? InstallerVersion { get; set; }

    public string Host { get; set; } = string.Empty;

    /// <summary>Computername des Servers, wie ihn der Installer vorgefunden hat.</summary>
    public string? HostName { get; set; }

    /// <summary>IPv4-Adressen des Servers, nur als Hinweis für die Fehlersuche.</summary>
    [JsonConverter(typeof(StringListConverter))]
    public List<string> Addresses { get; set; } = [];

    public int Port { get; set; } = DefaultPort;

    public string Database { get; set; } = string.Empty;

    public string Schema { get; set; } = "liftup";

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string SslMode { get; set; } = "Prefer";

    public string? ServerVersion { get; set; }

    /// <summary>Schemastand der Server-Datenbank; wird mit <see cref="Database.CurrentSchemaVersion"/> verglichen.</summary>
    public int SchemaVersion { get; set; }

    public int TableCount { get; set; }

    [JsonConverter(typeof(StringListConverter))]
    public List<string> Tables { get; set; } = [];

    /// <summary>Vollständiger Verbindungstext, wie ihn der Installer geschrieben hat (nur zur Anzeige).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Netze, die der Installer in <c>pg_hba.conf</c> freigegeben hat.</summary>
    [JsonConverter(typeof(StringListConverter))]
    public List<string> ClientCidr { get; set; } = [];

    public string? Notes { get; set; }

    [JsonIgnore]
    public string Endpoint => $"{Host}:{Port}";

    [JsonIgnore]
    public bool SchemaVersionMatchesLocal => SchemaVersion == LiftUp.Core.Data.Database.CurrentSchemaVersion;

    public static ServerProfile Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ValidationException("Es wurde keine Verbindungsdatei angegeben.");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ValidationException($"Die Verbindungsdatei konnte nicht gelesen werden: {exception.Message}");
        }

        return Parse(json);
    }

    public static ServerProfile Parse(string json)
    {
        ServerProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<ServerProfile>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ValidationException(
                $"Die Datei ist keine gültige Verbindungsdatei (JSON-Fehler: {exception.Message}).");
        }

        if (profile is null)
        {
            throw new ValidationException("Die Datei enthält keine Verbindungsdaten.");
        }

        profile.Validate();
        return profile;
    }

    /// <summary>Prüft die Angaben und bringt sie in eine einheitliche Form.</summary>
    public void Validate()
    {
        if (!string.Equals((Format ?? string.Empty).Trim(), FormatName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(
                $"Die Datei gehört nicht zu LiftUp. Erwartet wird \"format\": \"{FormatName}\", " +
                $"gefunden wurde \"{Format}\".");
        }

        if (FormatVersion < 1)
        {
            throw new ValidationException("Die Verbindungsdatei nennt keine Formatversion (formatVersion).");
        }

        if (FormatVersion > CurrentFormatVersion)
        {
            throw new ValidationException(
                $"Die Verbindungsdatei hat Formatversion {FormatVersion}; diese LiftUp-Version kennt nur " +
                $"{CurrentFormatVersion}. Bitte LiftUp aktualisieren.");
        }

        Host = (Host ?? string.Empty).Trim();
        if (Host.Length == 0)
        {
            throw new ValidationException("In der Verbindungsdatei fehlt der Server (host).");
        }

        if (Port is < 1 or > 65535)
        {
            throw new ValidationException($"Der Port in der Verbindungsdatei ist ungültig: {Port}.");
        }

        Database = (Database ?? string.Empty).Trim();
        if (Database.Length == 0)
        {
            throw new ValidationException("In der Verbindungsdatei fehlt die Datenbank (database).");
        }

        UserName = (UserName ?? string.Empty).Trim();
        if (UserName.Length == 0)
        {
            throw new ValidationException("In der Verbindungsdatei fehlt der Benutzer (username).");
        }

        Schema = string.IsNullOrWhiteSpace(Schema) ? "public" : Schema.Trim();
        if (!SchemaNamePattern.IsMatch(Schema))
        {
            throw new ValidationException(
                $"Der Schema-Name in der Verbindungsdatei ist ungültig: \"{Schema}\".");
        }

        var mode = AllowedSslModes.FirstOrDefault(candidate =>
            string.Equals(candidate, (SslMode ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        if (mode is null)
        {
            throw new ValidationException(
                $"Unbekannter SSL-Modus \"{SslMode}\". Erlaubt: {string.Join(", ", AllowedSslModes)}.");
        }

        SslMode = mode;
        Tables ??= [];
        Addresses ??= [];
        ClientCidr ??= [];
        Password ??= string.Empty;
    }

    /// <summary>
    /// Verbindungszeichenfolge für Npgsql. Die Bestandteile werden aus den einzelnen
    /// Angaben gebaut, damit ein veralteter Text aus der Datei nichts kaputt macht –
    /// Sonderzeichen im Passwort sind damit ebenfalls abgedeckt.
    /// </summary>
    public string BuildConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = UserName,
            Password = Password,
            ApplicationName = "LiftUp",
            SearchPath = Schema,
            Timeout = 15,
            CommandTimeout = 15,
            Pooling = true,
        };

        if (Enum.TryParse<Npgsql.SslMode>(SslMode, ignoreCase: true, out var sslMode))
        {
            builder.SslMode = sslMode;
        }

        return builder.ConnectionString;
    }
}

/// <summary>
/// Nimmt sowohl eine Liste als auch einen einzelnen Text an. Windows PowerShell 5.1
/// schreibt einfeldrige Listen als einzelnen Wert, und eine leere Liste als null.
/// </summary>
public sealed class StringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return [];

            case JsonTokenType.String:
                var single = reader.GetString();
                return string.IsNullOrWhiteSpace(single) ? [] : [single];

            case JsonTokenType.StartArray:
                var items = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        return items;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var value = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            items.Add(value);
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                throw new JsonException("Die Liste in der Verbindungsdatei ist nicht geschlossen.");

            default:
                throw new JsonException(
                    $"Unerwarteter Wert in der Verbindungsdatei: {reader.TokenType} statt einer Liste.");
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
