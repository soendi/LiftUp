using System.Globalization;
using System.Net.Sockets;
using LiftUp.Core.Data;
using Npgsql;

namespace LiftUp.Core.Sync;

/// <summary>
/// Abgleich mit der zentralen PostgreSQL-Datenbank über die eingelesene
/// Verbindungsdatei. Übertragen wird das Änderungsprotokoll: eigene Einträge
/// gehen mit ihrer lokalen Nummer hinauf, damit ein zweiter Lauf nichts doppelt
/// anlegt; Einträge der anderen Arbeitsplätze kommen über die Serversequenz
/// wieder herunter.
/// </summary>
public sealed class NpgsqlSyncTransport(ServerProfile profile, string deviceId) : ISyncTransport
{
    private const int PageSize = 200;

    private readonly ServerProfile _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    private readonly string _deviceId = string.IsNullOrWhiteSpace(deviceId) ? "unbekannt" : deviceId;

    public string Name => $"PostgreSQL {_profile.Endpoint}/{_profile.Database}";

    public string ScopeKey => $"{_profile.Endpoint}/{_profile.Database}/{_profile.Schema}";

    public bool IsConfigured => true;

    public async Task PushAsync(IReadOnlyList<ChangeLogEntry> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        try
        {
            await using var connection = new NpgsqlConnection(_profile.BuildConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureServerReadyAsync(connection, cancellationToken).ConfigureAwait(false);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var receivedUtc = DateTime.UtcNow.ToDb();

            foreach (var entry in changes)
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO "ChangeLog"
                        ("EntityType", "EntityId", "Operation", "Payload", "ChangedUtc",
                         "OriginDeviceId", "RowVersion", "OriginUser", "OriginEntryId", "ReceivedUtc")
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                    ON CONFLICT ("OriginDeviceId", "OriginEntryId") DO NOTHING;
                    """, connection, transaction);

                command.Parameters.AddWithValue(entry.EntityType);
                command.Parameters.AddWithValue(entry.EntityId);
                command.Parameters.AddWithValue(entry.Operation);
                command.Parameters.AddWithValue(entry.Payload);
                command.Parameters.AddWithValue(entry.ChangedUtc.ToDb());
                command.Parameters.AddWithValue(entry.OriginDeviceId.Length == 0 ? _deviceId : entry.OriginDeviceId);
                command.Parameters.AddWithValue(entry.RowVersion);
                command.Parameters.AddWithValue(
                    string.IsNullOrWhiteSpace(entry.OriginUser) ? DBNull.Value : entry.OriginUser);
                command.Parameters.AddWithValue(entry.Id);
                command.Parameters.AddWithValue(receivedUtc);

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            throw new SyncException($"Der Server hat die Änderungen abgelehnt: {exception.MessageText}");
        }
        catch (NpgsqlException exception)
        {
            throw new SyncException(DescribeConnectionError(exception));
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException)
        {
            throw new SyncException(DescribeConnectionError(exception));
        }
    }

    public async Task<IReadOnlyList<RemoteChange>> PullAsync(long sinceServerId, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_profile.BuildConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureServerReadyAsync(connection, cancellationToken).ConfigureAwait(false);

            await using var command = new NpgsqlCommand("""
                SELECT "Id", "EntityType", "EntityId", "Operation", "Payload", "ChangedUtc",
                       "OriginDeviceId", "RowVersion", "OriginUser"
                FROM "ChangeLog"
                WHERE "Id" > $1 AND "OriginDeviceId" <> $2
                ORDER BY "Id"
                LIMIT $3;
                """, connection);

            command.Parameters.AddWithValue(sinceServerId);
            command.Parameters.AddWithValue(_deviceId);
            command.Parameters.AddWithValue(PageSize);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var changes = new List<RemoteChange>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changes.Add(new RemoteChange(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
                    ParseTimestamp(reader.GetValue(5)),
                    reader.GetString(6),
                    reader.IsDBNull(8) ? string.Empty : reader.GetString(8)));
            }

            return changes;
        }
        catch (PostgresException exception)
        {
            throw new SyncException($"Der Server hat die Abfrage abgelehnt: {exception.MessageText}");
        }
        catch (NpgsqlException exception)
        {
            throw new SyncException(DescribeConnectionError(exception));
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException)
        {
            throw new SyncException(DescribeConnectionError(exception));
        }
    }

    /// <summary>
    /// Ergänzt Spalten und Index, die für das Wiedererkennen übertragener Einträge
    /// nötig sind. Ältere Installationen kennen sie noch nicht; die Anwendung darf
    /// das selbst nachziehen, weil sie Eigentümerin des Schemas ist.
    /// </summary>
    private static async Task EnsureServerReadyAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            ALTER TABLE "ChangeLog" ADD COLUMN IF NOT EXISTS "OriginEntryId" bigint;
            ALTER TABLE "ChangeLog" ADD COLUMN IF NOT EXISTS "ReceivedUtc" text;
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_ChangeLog_Origin"
                ON "ChangeLog" ("OriginDeviceId", "OriginEntryId");
            """, connection);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTime ParseTimestamp(object value)
    {
        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow;
    }

    private static string DescribeConnectionError(Exception exception)
    {
        var hint = exception.InnerException is SocketException
            ? " Läuft der Server, ist der Port richtig und ist der Server im Netz erreichbar?"
            : string.Empty;
        return $"Die Verbindung zum Server ist fehlgeschlagen: {exception.Message}{hint}";
    }
}
