using System.Text.Json;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Services;

/// <summary>Filter für das Änderungsprotokoll. Leere Felder schränken nicht ein.</summary>
public sealed record ChangeLogFilter(
    string? EntityType = null,
    string? Actor = null,
    string? Search = null,
    DateTime? From = null,
    DateTime? To = null,
    int Maximum = 500);

/// <summary>Ein Eintrag des Änderungsprotokolls, aufbereitet für die Anzeige.</summary>
public sealed record ChangeLogListItem
{
    public long Id { get; init; }
    public DateTime ChangedUtc { get; init; }
    public string EntityType { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string EntityId { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public string OriginDeviceId { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public bool IsSynced { get; init; }

    public string ChangedText => ChangedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public string EntityText => ChangeLogService.DescribeEntity(EntityType);

    public string OperationText => ChangeLogService.DescribeOperation(Operation);

    public string ActorText => string.IsNullOrWhiteSpace(Actor) ? "unbekannt" : Actor;

    public string DeviceText => ChangeLogService.Shorten(OriginDeviceId, 12);

    public string SyncText => IsSynced ? "abgeglichen" : "offen";

    /// <summary>Kurzfassung aus der Nutzlast, damit man den Datensatz erkennt.</summary>
    public string ObjectText => ChangeLogService.Summarize(Payload) is { Length: > 0 } summary
        ? summary
        : ChangeLogService.Shorten(EntityId, 12);

    /// <summary>Lesbare Nutzlast für die Detailanzeige.</summary>
    public string PayloadText => ChangeLogService.FormatPayload(Payload);
}

/// <summary>
/// Liest das Änderungsprotokoll (Outbox des späteren Abgleichs) und bereitet es
/// für die Anzeige auf. Geschrieben wird es von den Diensten über <c>ChangeLogWriter</c>.
/// </summary>
public sealed class ChangeLogService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly Database _database;
    private readonly Session _actor;

    public ChangeLogService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;
    }

    /// <summary>Im Protokoll vorkommende Bereiche, für die Auswahlliste.</summary>
    public IReadOnlyList<string> GetEntityTypes()
    {
        _actor.Require(Permission.ViewChangeLog);

        using var connection = _database.Open();
        using var command = Db.Command(connection,
            "SELECT DISTINCT EntityType FROM ChangeLog ORDER BY EntityType;");

        using var reader = command.ExecuteReader();
        var types = new List<string>();
        while (reader.Read())
        {
            types.Add(reader.Text(0));
        }

        return types;
    }

    /// <summary>Im Protokoll vorkommende Bearbeiter, für die Auswahlliste.</summary>
    public IReadOnlyList<string> GetActors()
    {
        _actor.Require(Permission.ViewChangeLog);

        using var connection = _database.Open();
        using var command = Db.Command(connection, """
            SELECT DISTINCT OriginUser FROM ChangeLog
            WHERE OriginUser IS NOT NULL AND trim(OriginUser) <> ''
            ORDER BY OriginUser;
            """);

        using var reader = command.ExecuteReader();
        var actors = new List<string>();
        while (reader.Read())
        {
            actors.Add(reader.Text(0));
        }

        return actors;
    }

    /// <summary>Protokolleinträge, neueste zuerst.</summary>
    public IReadOnlyList<ChangeLogListItem> Query(ChangeLogFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _actor.Require(Permission.ViewChangeLog);

        var maximum = filter.Maximum is > 0 and <= 5000 ? filter.Maximum : 500;

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            conditions.Add("EntityType = $entityType");
        }

        if (!string.IsNullOrWhiteSpace(filter.Actor))
        {
            conditions.Add("OriginUser = $actor");
        }

        // Die Zeitstempel liegen als ISO-8601 in UTC in der Datenbank; deshalb
        // werden die Grenzen ebenfalls so formatiert und als Text verglichen.
        if (filter.From is { } from)
        {
            conditions.Add("ChangedUtc >= $from");
        }

        if (filter.To is { } to)
        {
            conditions.Add("ChangedUtc < $to");
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            conditions.Add("(Payload LIKE $search OR Operation LIKE $search OR EntityId LIKE $search "
                           + "OR coalesce(OriginUser, '') LIKE $search)");
        }

        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);

        using var connection = _database.Open();
        using var command = Db.Command(connection, $"""
            SELECT Id, EntityType, EntityId, Operation, Payload, ChangedUtc, OriginDeviceId, RowVersion, SyncedUtc, OriginUser
            FROM ChangeLog
            {where}
            ORDER BY Id DESC
            LIMIT $limit;
            """);

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            command.With("$entityType", filter.EntityType);
        }

        if (!string.IsNullOrWhiteSpace(filter.Actor))
        {
            command.With("$actor", filter.Actor);
        }

        if (filter.From is { } fromValue)
        {
            command.With("$from", DateTime.SpecifyKind(fromValue.Date, DateTimeKind.Local).ToUniversalTime().ToDb());
        }

        if (filter.To is { } toValue)
        {
            command.With("$to", DateTime.SpecifyKind(toValue.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime().ToDb());
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            command.With("$search", $"%{filter.Search.Trim()}%");
        }

        command.With("$limit", maximum);

        using var reader = command.ExecuteReader();
        var entries = new List<ChangeLogListItem>();
        while (reader.Read())
        {
            entries.Add(new ChangeLogListItem
            {
                Id = reader.Int64(0),
                EntityType = reader.Text(1),
                EntityId = reader.Text(2),
                Operation = reader.Text(3),
                Payload = reader.Text(4),
                ChangedUtc = reader.Date(5),
                OriginDeviceId = reader.Text(6),
                IsSynced = !reader.IsDBNull(8),
                Actor = reader.Text(9),
            });
        }

        return entries;
    }

    public static string DescribeEntity(string entityType) => entityType switch
    {
        "Project" => "Projekt",
        "User" => "Benutzer",
        "Role" => "Rolle",
        "Template" => "Vorlage",
        "Document" => "Dokument",
        "DocumentType" => "Dokumenttyp",
        "Image" => "Bild",
        "Snippet" => "Textbaustein",
        _ => entityType,
    };

    public static string DescribeOperation(string operation) => operation switch
    {
        "Create" => "angelegt",
        "Update" => "geändert",
        "Delete" => "gelöscht",
        "Archive" => "archiviert",
        "Restore" => "wiederhergestellt",
        "Activate" => "aktiviert",
        "Deactivate" => "deaktiviert",
        "Rename" => "umbenannt",
        "PasswordReset" => "Passwort zurückgesetzt",
        "PasswordChanged" => "Passwort geändert",
        _ => operation,
    };

    public static string Shorten(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    /// <summary>Zieht die wichtigsten Werte aus der Nutzlast, z. B. „2026-0001 · Umbau Halle“.</summary>
    public static string Summarize(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var values = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        values.Add(property.Value.GetString() ?? string.Empty);
                        break;
                    case JsonValueKind.Number:
                        values.Add(property.Value.ToString());
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        values.Add(property.Value.GetBoolean() ? "ja" : "nein");
                        break;
                }

                if (values.Count == 3)
                {
                    break;
                }
            }

            var text = string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
            return Shorten(text, 70);
        }
        catch (JsonException)
        {
            return Shorten(payload, 70);
        }
    }

    /// <summary>Formatiert die Nutzlast eingerückt; bei ungültigem JSON bleibt der Rohtext.</summary>
    public static string FormatPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            return payload;
        }
    }
}
