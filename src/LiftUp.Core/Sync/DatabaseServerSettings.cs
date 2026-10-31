using LiftUp.Core.Domain;
using Npgsql;

namespace LiftUp.Core.Sync;

/// <summary>
/// Zugangsdaten der zentralen PostgreSQL-Datenbank. Sie werden lokal in der
/// SQLite-Datei abgelegt (das Passwort geschützt) und können aus der vom
/// Server-Setup erzeugten Verbindungsdatei übernommen werden.
/// </summary>
public sealed record DatabaseServerSettings
{
    public const int DefaultPort = 5432;
    public const string DefaultDatabase = "liftup";
    public const string DefaultUser = "liftup";

    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = DefaultPort;
    public string Database { get; init; } = DefaultDatabase;
    public string User { get; init; } = DefaultUser;
    public string Password { get; init; } = string.Empty;
    public string SslMode { get; init; } = "Prefer";

    /// <summary>Auswahl für die Oberfläche; die Reihenfolge ist die der Combobox.</summary>
    public static IReadOnlyList<string> SslModes { get; } = ["Prefer", "Require", "VerifyFull", "Disable"];

    /// <summary>Ohne Server und Benutzer ist kein Abgleich möglich.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(Database) &&
        !string.IsNullOrWhiteSpace(User) &&
        Port is > 0 and <= 65535;

    /// <summary>Prüft die Eingaben und liefert verständliche Meldungen zurück.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Host))
        {
            errors.Add("Der Server (Rechnername oder IP-Adresse) fehlt.");
        }

        if (Port is <= 0 or > 65535)
        {
            errors.Add("Der Port muss zwischen 1 und 65535 liegen.");
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            errors.Add("Der Name der Datenbank fehlt.");
        }

        if (string.IsNullOrWhiteSpace(User))
        {
            errors.Add("Der Benutzername fehlt.");
        }

        if (!SslModes.Contains(SslMode, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Der SSL-Modus „{SslMode}“ ist unbekannt.");
        }

        return errors;
    }

    /// <summary>Wirft eine <see cref="ValidationException"/> mit allen Meldungen.</summary>
    public DatabaseServerSettings RequireValid()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new ValidationException(string.Join(" ", errors));
        }

        return this;
    }

    /// <summary>Beschreibung ohne Passwort – für Statuszeilen und Protokolle.</summary>
    public string Describe() => $"{User.Trim()} auf {Host.Trim()}:{Port}, Datenbank {Database.Trim()}";

    public string BuildConnectionString() => BuildConnectionString(Password);

    /// <summary>
    /// Verbindungszeichenfolge für Npgsql. Bewusst mit kurzem Verbindungs-Timeout,
    /// damit ein abgeschalteter Server die Oberfläche nicht blockiert.
    /// </summary>
    public string BuildConnectionString(string password)
    {
        var mode = Enum.TryParse<Npgsql.SslMode>(SslMode, ignoreCase: true, out var parsed)
            ? parsed
            : Npgsql.SslMode.Prefer;

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host.Trim(),
            Port = Port,
            Database = Database.Trim(),
            Username = User.Trim(),
            Password = password,
            ApplicationName = "LiftUp",
            Timeout = 8,
            CommandTimeout = 30,
            SslMode = mode,
        };

        return builder.ConnectionString;
    }
}
