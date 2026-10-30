using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Data;

public sealed class ChangeLogEntry
{
    public long Id { get; init; }
    public string EntityType { get; init; } = string.Empty;
    public string EntityId { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public DateTime ChangedUtc { get; init; }
    public string OriginDeviceId { get; init; } = string.Empty;
    public long RowVersion { get; init; }
    public DateTime? SyncedUtc { get; init; }
}

/// <summary>
/// Schreibt jede Änderung in das Protokoll <c>ChangeLog</c> – als Outbox für den
/// späteren Abgleich mit PostgreSQL. Der Eintrag entsteht in derselben
/// Transaktion wie die eigentliche Änderung, damit lokal nichts verloren geht.
/// Passwörter und Hashes gehören niemals in die Nutzlast.
/// </summary>
public sealed class ChangeLogWriter(Database database, string? actorName = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private string? _deviceId;

    public string DeviceId => _deviceId ??= database.GetDeviceId();

    public void Record(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string entityType,
        string entityId,
        string operation,
        object payload,
        long rowVersion)
    {
        using var command = Db.Command(connection, """
            INSERT INTO ChangeLog
                (EntityType, EntityId, Operation, Payload, ChangedUtc, OriginDeviceId, RowVersion, OriginUser)
            VALUES ($entityType, $entityId, $operation, $payload, $changedUtc, $origin, $rowVersion, $originUser);
            """, transaction);
        command.With("$entityType", entityType)
            .With("$entityId", entityId)
            .With("$operation", operation)
            .With("$payload", JsonSerializer.Serialize(payload, SerializerOptions))
            .With("$changedUtc", DateTime.UtcNow.ToDb())
            .With("$origin", DeviceId)
            .With("$rowVersion", rowVersion)
            .With("$originUser", string.IsNullOrWhiteSpace(actorName) ? (object)DBNull.Value : actorName);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ChangeLogEntry> GetPending(int maximum = 500)
    {
        using var connection = database.Open();
        return GetPending(connection, null, maximum);
    }

    public static IReadOnlyList<ChangeLogEntry> GetPending(SqliteConnection connection, SqliteTransaction? transaction, int maximum = 500)
    {
        using var command = Db.Command(connection, """
            SELECT Id, EntityType, EntityId, Operation, Payload, ChangedUtc, OriginDeviceId, RowVersion, SyncedUtc
            FROM ChangeLog
            WHERE SyncedUtc IS NULL
            ORDER BY Id
            LIMIT $limit;
            """, transaction);
        command.With("$limit", maximum);

        using var reader = command.ExecuteReader();
        var entries = new List<ChangeLogEntry>();
        while (reader.Read())
        {
            entries.Add(new ChangeLogEntry
            {
                Id = reader.Int64(0),
                EntityType = reader.Text(1),
                EntityId = reader.Text(2),
                Operation = reader.Text(3),
                Payload = reader.Text(4),
                ChangedUtc = reader.Date(5),
                OriginDeviceId = reader.Text(6),
                RowVersion = reader.Int64(7),
                SyncedUtc = reader.DateOrNull(8),
            });
        }

        return entries;
    }

    public void MarkSynced(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        MarkSynced(connection, transaction, ids);
        transaction.Commit();
    }

    public static void MarkSynced(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var parameters = string.Join(", ", ids.Select((_, index) => $"$id{index}"));
        using var command = Db.Command(connection,
            $"UPDATE ChangeLog SET SyncedUtc = $syncedUtc WHERE Id IN ({parameters});", transaction);
        command.With("$syncedUtc", DateTime.UtcNow.ToDb());
        for (var index = 0; index < ids.Count; index++)
        {
            command.With($"$id{index}", ids[index]);
        }

        command.ExecuteNonQuery();
    }
}
