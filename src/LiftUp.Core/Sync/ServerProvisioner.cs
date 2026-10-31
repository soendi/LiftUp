using LiftUp.Core.Domain;
using Npgsql;

namespace LiftUp.Core.Sync;

/// <summary>Was beim Einrichten der Server-Datenbank entstanden ist.</summary>
public sealed record ProvisionResult(string ServerVersion, bool RoleCreated, bool DatabaseCreated);

/// <summary>
/// Legt auf einem PostgreSQL-Server alles an, was LiftUp braucht: die Rolle des
/// Clients, die Datenbank, das Schema und die Tabellen. Wird vom Server-Setup
/// verwendet und ist wiederholbar – vorhandene Teile bleiben unangetastet.
/// </summary>
public sealed class ServerProvisioner(
    DatabaseServerSettings administrator,
    DatabaseServerSettings target)
{
    /// <summary>
    /// Richtet Rolle und Datenbank ein. <paramref name="administrator"/> ist die
    /// Verbindung als Superuser (meist <c>postgres</c>), <paramref name="target"/>
    /// beschreibt, was der Client später benutzt.
    /// </summary>
    public async Task<ProvisionResult> ProvisionAsync(CancellationToken cancellationToken = default)
    {
        administrator.RequireValid();
        target.RequireValid();

        await using var adminConnection = new NpgsqlConnection(administrator.BuildConnectionString());
        await adminConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var serverVersion = await ReadServerVersionAsync(adminConnection, cancellationToken).ConfigureAwait(false);
        var roleCreated = await EnsureRoleAsync(adminConnection, cancellationToken).ConfigureAwait(false);
        var databaseCreated = await EnsureDatabaseAsync(adminConnection, cancellationToken).ConfigureAwait(false);

        await using var targetConnection = new NpgsqlConnection(target.BuildConnectionString(target.Password));
        await targetConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(targetConnection,
            $"CREATE SCHEMA IF NOT EXISTS liftup AUTHORIZATION {Quote(target.User)};", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(targetConnection,
            $"ALTER SCHEMA liftup OWNER TO {Quote(target.User)};", cancellationToken).ConfigureAwait(false);

        await ServerSchema.EnsureAsync(targetConnection, cancellationToken).ConfigureAwait(false);
        await GrantAsync(targetConnection, cancellationToken).ConfigureAwait(false);

        return new ProvisionResult(serverVersion, roleCreated, databaseCreated);
    }

    private async Task<bool> EnsureRoleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM pg_roles WHERE rolname = @name;";
        exists.Parameters.AddWithValue("name", target.User);
        var found = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (found is not null)
        {
            return false;
        }

        // Rollen- und Passwortwerte werden über format(%I/%L) sicher eingesetzt.
        await using var create = connection.CreateCommand();
        create.CommandText = "SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', @name, @password);";
        create.Parameters.AddWithValue("name", target.User);
        create.Parameters.AddWithValue("password", target.Password);
        var sql = (string)(await create.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> EnsureDatabaseAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name;";
        exists.Parameters.AddWithValue("name", target.Database);
        var found = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (found is not null)
        {
            return false;
        }

        await using var create = connection.CreateCommand();
        create.CommandText = "SELECT format('CREATE DATABASE %I OWNER %I', @database, @owner);";
        create.Parameters.AddWithValue("database", target.Database);
        create.Parameters.AddWithValue("owner", target.User);
        var sql = (string)(await create.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task GrantAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var user = Quote(target.User);
        var statements = string.Join("\n", new[]
        {
            $"GRANT USAGE ON SCHEMA liftup TO {user};",
            $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA liftup TO {user};",
            $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA liftup TO {user};",
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA liftup GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {user};",
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA liftup GRANT USAGE, SELECT ON SEQUENCES TO {user};",
        });

        await ExecuteAsync(connection, statements, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadServerVersionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW server_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string ?? "unbekannt";
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Setzt einen Bezeichner in doppelte Anführungszeichen.</summary>
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
