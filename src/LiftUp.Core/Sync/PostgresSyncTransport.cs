using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Npgsql;

namespace LiftUp.Core.Sync;

/// <summary>
/// Abgleich über direkten PostgreSQL-Zugriff. Gesendet werden vollständige
/// Datensätze; der Server behält je Datensatz den höchsten <c>RowVersion</c> und
/// führt jede angenommene Änderung in sein fortlaufendes Protokoll ein. Clients
/// holen sich von dort alles, was seit ihrer letzten Nummer dazukam – eigene
/// Änderungen ausgenommen, damit nichts doppelt läuft.
/// </summary>
public sealed class PostgresSyncTransport(Database database, DatabaseServerSettings settings) : ISyncTransport
{
    public string Name => $"PostgreSQL {settings.Host.Trim()}:{settings.Port}/{settings.Database.Trim()}";

    public bool IsConfigured => settings.IsComplete;

    /// <summary>Prüft die Verbindung und meldet Datenbank und Benutzer zurück.</summary>
    public static async Task<string> TestAsync(
        DatabaseServerSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings.RequireValid();

        await using var connection = new NpgsqlConnection(settings.BuildConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database(), current_user, current_setting('server_version');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ValidationException("Der Server hat keine Verbindungsdaten zurückgegeben.");
        }

        return $"Verbunden mit „{reader.GetString(0)}“ als „{reader.GetString(1)}“ (PostgreSQL {reader.GetString(2)}).";
    }

    /// <summary>
    /// Sendet ein Paket in einer Transaktion. Sätze, bei denen der Server denselben
    /// oder einen neueren Stand hat, werden nicht übernommen, sondern als Konflikt
    /// zurückgegeben – damit der Client auf den gemeinsamen Stand kommt.
    /// </summary>
    public async Task<PushResult> PushAsync(
        IReadOnlyList<EntitySnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        if (snapshots.Count == 0)
        {
            return PushResult.Empty;
        }

        await using var connection = new NpgsqlConnection(settings.BuildConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ServerSchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var accepted = 0;
        var conflicts = new List<EntitySnapshot>();

        // Feste Reihenfolge der Sperren: zwei Arbeitsplätze, die dieselben Sätze
        // senden, sperren sie dadurch in derselben Folge und verklemmen sich nicht.
        var ordered = snapshots
            .OrderBy(snapshot => snapshot.EntityType, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.EntityId, StringComparer.Ordinal);

        foreach (var snapshot in ordered)
        {
            var existing = await ReadEntityAsync(connection, transaction, snapshot, cancellationToken)
                .ConfigureAwait(false);

            // Der Server behält den neueren Stand; Gleichstand bedeutet, dass der
            // Satz bereits bekannt ist. In beiden Fällen gilt der Serverstand.
            if (existing is { } current && current.RowVersion >= snapshot.RowVersion)
            {
                conflicts.Add(current);
                continue;
            }

            await UpsertEntityAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
            await InsertChangeAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
            accepted++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PushResult(accepted, conflicts);
    }

    public async Task<IReadOnlyList<RemoteChange>> PullAsync(long afterChangeId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(settings.BuildConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ServerSchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);

        var deviceId = database.GetDeviceId();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, entity_type, entity_id, data::text, row_version, is_deleted, changed_utc, origin_device
            FROM liftup.change_log
            WHERE id > @after AND origin_device <> @device
            ORDER BY id
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("after", afterChangeId);
        command.Parameters.AddWithValue("device", deviceId);
        command.Parameters.AddWithValue("limit", 1000);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var changes = new List<RemoteChange>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new RemoteChange(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetBoolean(5),
                reader.GetFieldValue<DateTime>(6).ToUniversalTime(),
                reader.GetString(7)));
        }

        return changes;
    }

    /// <summary>Liest den Serverstand eines Satzes und sperrt ihn für die Transaktion.</summary>
    private static async Task<EntitySnapshot?> ReadEntityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT entity_type, entity_id, data::text, row_version, is_deleted, changed_utc, origin_device
            FROM liftup.entities
            WHERE entity_type = @type AND entity_id = @id
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("type", snapshot.EntityType);
        command.Parameters.AddWithValue("id", snapshot.EntityId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new EntitySnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetBoolean(4),
            reader.GetFieldValue<DateTime>(5).ToUniversalTime(),
            reader.GetString(6));
    }

    private static async Task UpsertEntityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO liftup.entities
                (entity_type, entity_id, data, row_version, is_deleted, changed_utc, origin_device, received_utc)
            VALUES
                (@type, @id, CAST(@data AS jsonb), @rowVersion, @deleted, @changedUtc, @device, now())
            ON CONFLICT (entity_type, entity_id) DO UPDATE SET
                data          = EXCLUDED.data,
                row_version   = EXCLUDED.row_version,
                is_deleted    = EXCLUDED.is_deleted,
                changed_utc   = EXCLUDED.changed_utc,
                origin_device = EXCLUDED.origin_device,
                received_utc  = now();
            """;
        AddSnapshotParameters(command, snapshot);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO liftup.change_log
                (entity_type, entity_id, data, row_version, is_deleted, changed_utc, origin_device)
            VALUES
                (@type, @id, CAST(@data AS jsonb), @rowVersion, @deleted, @changedUtc, @device);
            """;
        AddSnapshotParameters(command, snapshot);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddSnapshotParameters(NpgsqlCommand command, EntitySnapshot snapshot)
    {
        command.Parameters.AddWithValue("type", snapshot.EntityType);
        command.Parameters.AddWithValue("id", snapshot.EntityId);
        command.Parameters.AddWithValue("data", snapshot.Data);
        command.Parameters.AddWithValue("rowVersion", snapshot.RowVersion);
        command.Parameters.AddWithValue("deleted", snapshot.IsDeleted);
        command.Parameters.AddWithValue("changedUtc", snapshot.ChangedUtc.ToUniversalTime());
        command.Parameters.AddWithValue("device", snapshot.OriginDeviceId);
    }
}
