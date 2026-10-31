using System.Text.Json;
using LiftUp.Core.Data;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Sync;

/// <summary>Ein vollständiger Datensatz, wie er zum Server geht.</summary>
public sealed record EntitySnapshot(
    string EntityType,
    string EntityId,
    string Data,
    long RowVersion,
    bool IsDeleted,
    DateTime ChangedUtc,
    string OriginDeviceId);

/// <summary>Eine Änderung, die vom Server hereinkommt.</summary>
public sealed record RemoteChange(
    long ChangeId,
    string EntityType,
    string EntityId,
    string Data,
    long RowVersion,
    bool IsDeleted,
    DateTime ChangedUtc,
    string OriginDeviceId);

/// <summary>
/// Liest zu den offenen Protokolleinträgen den aktuellen Datensatz aus der
/// lokalen Datenbank. Die Protokollnotiz selbst enthält nur eine Kurzfassung
/// (etwa Name und Nummer) und taugt nicht zum Übertragen ganzer Sätze.
/// </summary>
public static class LocalSnapshotReader
{
    public static IReadOnlyList<EntitySnapshot> Read(
        Database database,
        IReadOnlyList<ChangeLogEntry> changes)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(changes);

        // Mehrere Änderungen am selben Datensatz ergeben einen Snapshot; der
        // jüngste Protokolleintrag bestimmt die Reihenfolge.
        var wanted = changes
            .Where(change => SyncTableMap.TryGetTable(change.EntityType, out _))
            .GroupBy(change => (change.EntityType, change.EntityId))
            .Select(group => group.OrderBy(change => change.Id).Last())
            .ToList();

        if (wanted.Count == 0)
        {
            return [];
        }

        using var connection = database.Open();
        var deviceId = Database.GetDeviceId(connection, null);
        var snapshots = new List<EntitySnapshot>(wanted.Count);

        foreach (var change in wanted)
        {
            if (!SyncTableMap.TryGetTable(change.EntityType, out var table))
            {
                continue;
            }

            var snapshot = ReadRow(connection, table, change);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot with { OriginDeviceId = deviceId });
            }
        }

        return snapshots;
    }

    private static EntitySnapshot? ReadRow(SqliteConnection connection, string table, ChangeLogEntry change)
    {
        using var command = Db.Command(connection, $"SELECT * FROM \"{table}\" WHERE Id = $id;");
        command.With("$id", change.EntityId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            // Datensatz ist inzwischen endgültig entfernt – dann gibt es nichts zu senden.
            return null;
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var rowVersion = change.RowVersion;
        var isDeleted = false;

        for (var index = 0; index < reader.FieldCount; index++)
        {
            var name = reader.GetName(index);
            var value = reader.IsDBNull(index) ? null : reader.GetValue(index);
            values[name] = value;

            if (string.Equals(name, "RowVersion", StringComparison.OrdinalIgnoreCase) && value is not null)
            {
                rowVersion = Convert.ToInt64(value);
            }

            if (string.Equals(name, "IsDeleted", StringComparison.OrdinalIgnoreCase) && value is not null)
            {
                isDeleted = Convert.ToInt64(value) != 0;
            }
        }

        return new EntitySnapshot(
            change.EntityType,
            change.EntityId,
            JsonSerializer.Serialize(values),
            rowVersion,
            isDeleted,
            change.ChangedUtc,
            change.OriginDeviceId);
    }
}
