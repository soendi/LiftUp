using System.Text.Json;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Sync;

/// <summary>Ergebnis eines hereinkommenden Abgleichs.</summary>
public sealed record LocalApplyResult(int Applied, int Skipped, int Failed, IReadOnlyList<string> Errors)
{
    public static LocalApplyResult Empty { get; } = new(0, 0, 0, []);
}

/// <summary>
/// Schreibt hereinkommende Datensätze in die lokale SQLite-Datenbank.
///
/// Beim Holen gilt: Der höhere <c>RowVersion</c> gewinnt; ist der lokale Satz
/// neuer, bleibt er stehen und geht beim nächsten Senden zum Server.
/// Bei einem Konflikt aus dem Senden gilt dagegen der Serverstand – auch bei
/// gleicher Version, damit beide Arbeitsplätze denselben Satz behalten.
/// </summary>
public static class LocalChangeApplier
{
    public static LocalApplyResult Apply(Database database, IReadOnlyList<RemoteChange> changes)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
        {
            return LocalApplyResult.Empty;
        }

        var applied = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var change in changes.OrderBy(item => item.ChangeId))
        {
            ApplyCore(database, change.EntityType, change.EntityId, change.Data, change.RowVersion,
                serverWinsOnTie: false, ref applied, ref skipped, errors);
        }

        return new LocalApplyResult(applied, skipped, errors.Count, errors);
    }

    /// <summary>
    /// Übernimmt die Sätze, die der Server beim Senden abgelehnt hat. Hier gilt
    /// sein Stand auch bei gleicher Version; sonst blieben zwei Arbeitsplätze
    /// nach einer gleichzeitigen Änderung dauerhaft verschieden.
    /// </summary>
    public static LocalApplyResult ApplyServerState(Database database, IReadOnlyList<EntitySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(snapshots);

        if (snapshots.Count == 0)
        {
            return LocalApplyResult.Empty;
        }

        var applied = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var snapshot in snapshots)
        {
            ApplyCore(database, snapshot.EntityType, snapshot.EntityId, snapshot.Data, snapshot.RowVersion,
                serverWinsOnTie: true, ref applied, ref skipped, errors);
        }

        return new LocalApplyResult(applied, skipped, errors.Count, errors);
    }

    private static void ApplyCore(
        Database database,
        string entityType,
        string entityId,
        string data,
        long rowVersion,
        bool serverWinsOnTie,
        ref int applied,
        ref int skipped,
        List<string> errors)
    {
        if (!SyncTableMap.TryGetTable(entityType, out var table))
        {
            skipped++;
            return;
        }

        try
        {
            if (ApplyOne(database, table, entityId, data, rowVersion, serverWinsOnTie))
            {
                applied++;
            }
            else
            {
                skipped++;
            }
        }
        catch (Exception exception) when (exception is SqliteException or JsonException or ValidationException)
        {
            errors.Add($"{entityType} {entityId}: {exception.Message}");
        }
    }

    private static bool ApplyOne(
        Database database,
        string table,
        string entityId,
        string data,
        long rowVersion,
        bool serverWinsOnTie)
    {
        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ValidationException("Die Nutzlast ist kein JSON-Objekt.");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = ToSqliteValue(property.Value);
        }

        values.TryAdd("Id", entityId);

        using var connection = database.Open();

        // Lesen und Schreiben in einer Transaktion: Zwei gleichzeitige Läufe
        // können so nicht denselben Satz unterschiedlich beurteilen.
        using var transaction = connection.BeginTransaction();

        var columns = ReadColumns(connection, transaction, table);
        var usable = values.Where(pair => columns.Contains(pair.Key)).ToList();
        if (usable.Count == 0 || !values.ContainsKey("Id"))
        {
            throw new ValidationException("Der Datensatz enthält keine passenden Spalten.");
        }

        var localVersion = ReadRowVersion(connection, transaction, table, entityId);
        if (localVersion is { } current && (serverWinsOnTie ? rowVersion < current : rowVersion <= current))
        {
            return false;
        }

        using var command = localVersion is null
            ? InsertCommand(connection, transaction, table, usable)
            : UpdateCommand(connection, transaction, table, usable);

        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    private static SqliteCommand InsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<KeyValuePair<string, object?>> values)
    {
        var names = string.Join(", ", values.Select(pair => $"\"{pair.Key}\""));
        var parameters = string.Join(", ", values.Select((_, index) => $"$p{index}"));
        var command = Db.Command(connection, $"INSERT INTO \"{table}\" ({names}) VALUES ({parameters});", transaction);

        for (var index = 0; index < values.Count; index++)
        {
            command.With($"$p{index}", values[index].Value);
        }

        return command;
    }

    private static SqliteCommand UpdateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<KeyValuePair<string, object?>> values)
    {
        var assignments = values
            .Where(pair => !string.Equals(pair.Key, "Id", StringComparison.OrdinalIgnoreCase))
            .Select((pair, index) => $"\"{pair.Key}\" = $p{index}")
            .ToList();

        if (assignments.Count == 0)
        {
            throw new ValidationException("Der Datensatz enthält keine änderbaren Werte.");
        }

        var command = Db.Command(connection,
            $"UPDATE \"{table}\" SET {string.Join(", ", assignments)} WHERE Id = $id;", transaction);

        var index = 0;
        foreach (var pair in values.Where(pair => !string.Equals(pair.Key, "Id", StringComparison.OrdinalIgnoreCase)))
        {
            command.With($"$p{index}", pair.Value);
            index++;
        }

        command.With("$id", values.First(pair => string.Equals(pair.Key, "Id", StringComparison.OrdinalIgnoreCase)).Value);
        return command;
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = Db.Command(connection, $"PRAGMA table_info(\"{table}\");", transaction);
        using var reader = command.ExecuteReader();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static long? ReadRowVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string id)
    {
        using var command = Db.Command(connection, $"SELECT RowVersion FROM \"{table}\" WHERE Id = $id;", transaction);
        command.With("$id", id);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static object? ToSqliteValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var number) ? number : element.GetDouble(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };
}
