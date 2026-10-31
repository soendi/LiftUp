using System.Globalization;
using System.Net.Sockets;
using LiftUp.Core.Data;
using Npgsql;

namespace LiftUp.Core.Diagnostics;

public sealed record ServerCheckResult(
    bool Success,
    string Message,
    string ServerVersion,
    int SchemaVersion,
    int TableCount,
    IReadOnlyList<string>? MissingTables = null)
{
    /// <summary>Tabellen, die lokal existieren, auf dem Server aber fehlen.</summary>
    public IReadOnlyList<string> Missing => MissingTables ?? [];
}

/// <summary>
/// Prüft eine eingelesene Verbindungsdatei gegen den echten PostgreSQL-Server:
/// Anmeldung, Schemastand, Tabellenzahl und – wenn die lokalen Tabellen bekannt
/// sind – ob eine Schemaänderung der Anwendung auf dem Server angekommen ist.
/// Anders als die reine TCP-Prüfung in <see cref="DatabaseDiagnostics"/> deckt das
/// auch Benutzer, Passwort und Datenbank ab.
/// </summary>
public static class ServerDatabaseChecker
{
    public static async Task<ServerCheckResult> CheckAsync(
        ServerProfile profile,
        IEnumerable<string>? localTables = null,
        CancellationToken cancellationToken = default)
    {
        profile.Validate();

        try
        {
            await using var connection = new NpgsqlConnection(profile.BuildConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var serverVersion = Convert.ToString(
                await ScalarAsync(connection, "SHOW server_version;", null, cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) ?? string.Empty;

            var serverTables = await ListTablesAsync(connection, profile.Schema, cancellationToken).ConfigureAwait(false);
            var missing = localTables is null
                ? []
                : localTables
                    .Where(table => !serverTables.Contains(table))
                    .OrderBy(table => table, StringComparer.Ordinal)
                    .ToArray();

            var quotedSchema = "\"" + profile.Schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            object? version;
            try
            {
                version = await ScalarAsync(
                    connection,
                    $"SELECT \"Version\" FROM {quotedSchema}.\"SchemaInfo\" LIMIT 1;",
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == "42P01")
            {
                return new ServerCheckResult(
                    false,
                    $"Die Anmeldung hat funktioniert, aber das Schema \"{profile.Schema}\" fehlt. " +
                    "Bitte installer/Install-ServerDatabase.ps1 erneut ausführen.",
                    serverVersion,
                    0,
                    0);
            }

            var schemaVersion = version is null or DBNull
                ? 0
                : Convert.ToInt32(version, CultureInfo.InvariantCulture);

            var message =
                $"Verbindung hergestellt: PostgreSQL {serverVersion}, Schema \"{profile.Schema}\" " +
                $"(Version {schemaVersion}), {serverTables.Count} Tabelle(n).";

            if (schemaVersion != Database.CurrentSchemaVersion)
            {
                message += $" Achtung: die lokale Datenbank steht auf Schema {Database.CurrentSchemaVersion}.";
            }

            if (missing.Length > 0)
            {
                message +=
                    $" Auf dem Server fehlen Tabellen: {string.Join(", ", missing)}. " +
                    "Bitte installer/Install-ServerDatabase.ps1 erneut ausführen – das Skript ergänzt nur Fehlendes " +
                    "und lässt vorhandene Daten stehen.";
            }

            return new ServerCheckResult(true, message, serverVersion, schemaVersion, serverTables.Count, missing);
        }
        catch (PostgresException exception)
        {
            return new ServerCheckResult(
                false,
                $"Die Datenbank hat die Verbindung abgelehnt: {exception.MessageText}",
                string.Empty,
                0,
                0);
        }
        catch (NpgsqlException exception)
        {
            var hint = exception.InnerException is SocketException
                ? " Läuft der Server, ist der Port richtig und ist der Server im Netz erreichbar?"
                : string.Empty;
            return new ServerCheckResult(
                false,
                $"Die Verbindung ist fehlgeschlagen: {exception.Message}{hint}",
                string.Empty,
                0,
                0);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return new ServerCheckResult(
                false,
                $"Die Verbindung ist fehlgeschlagen: {exception.Message}",
                string.Empty,
                0,
                0);
        }
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        string? parameter,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (parameter is not null)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ListTablesAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = $1 AND table_type = 'BASE TABLE';
            """, connection);
        command.Parameters.AddWithValue(schema);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
