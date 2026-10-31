using Npgsql;

namespace LiftUp.Core.Sync;

/// <summary>
/// Schema der zentralen Datenbank. Der Server hält je Datensatz den letzten
/// Stand als JSON und führt zusätzlich ein fortlaufendes Änderungsprotokoll,
/// aus dem sich die Clients bedienen.
/// </summary>
public static class ServerSchema
{
    public const int Version = 1;

    public const string Sql = """
        CREATE SCHEMA IF NOT EXISTS liftup;

        CREATE TABLE IF NOT EXISTS liftup.info (
            id             integer PRIMARY KEY CHECK (id = 1),
            schema_version integer NOT NULL,
            created_utc    timestamptz NOT NULL DEFAULT now(),
            updated_utc    timestamptz NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS liftup.entities (
            entity_type   text        NOT NULL,
            entity_id     text        NOT NULL,
            data          jsonb       NOT NULL,
            row_version   bigint      NOT NULL,
            is_deleted    boolean     NOT NULL DEFAULT false,
            changed_utc   timestamptz NOT NULL,
            origin_device text        NOT NULL,
            received_utc  timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (entity_type, entity_id)
        );

        CREATE TABLE IF NOT EXISTS liftup.change_log (
            id            bigserial   PRIMARY KEY,
            entity_type   text        NOT NULL,
            entity_id     text        NOT NULL,
            data          jsonb       NOT NULL,
            row_version   bigint      NOT NULL,
            is_deleted    boolean     NOT NULL DEFAULT false,
            changed_utc   timestamptz NOT NULL,
            origin_device text        NOT NULL,
            received_utc  timestamptz NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_change_log_entity
            ON liftup.change_log (entity_type, entity_id);

        CREATE TABLE IF NOT EXISTS liftup.devices (
            device_id     text PRIMARY KEY,
            app_version   text NULL,
            last_seen_utc timestamptz NOT NULL DEFAULT now()
        );

        INSERT INTO liftup.info (id, schema_version)
        VALUES (1, 1)
        ON CONFLICT (id) DO NOTHING;
        """;

    /// <summary>Legt Schema, Tabellen und Indizes an, falls sie fehlen.</summary>
    public static async Task EnsureAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
