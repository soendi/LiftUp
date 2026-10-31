using System.Globalization;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using LiftUp.Core.Sync;
using Npgsql;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Abgleich zweier Arbeitsplätze gegen einen echten PostgreSQL-Server.
///
/// Die Tests laufen nur, wenn <c>LIFTUP_TEST_POSTGRES</c> eine Verbindung zum
/// Superuser enthält (etwa <c>Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres</c>).
/// Starten mit <c>tools/postgres-sync-test.sh</c>; ohne Server werden sie übersprungen.
/// Jeder Test bekommt eine eigene Datenbank, damit nichts kollidiert.
/// </summary>
public sealed class PostgresSyncEndToEndTests
{
    [PostgresFact]
    public async Task Zwei_arbeitsplaetze_gleichen_projekte_ab()
    {
        using var server = new PostgresServer();
        using var clientA = new Client(server.Settings);
        using var clientB = new Client(server.Settings);

        for (var index = 1; index <= 3; index++)
        {
            clientA.Projects.Create(new ProjectInput
            {
                Name = $"Projekt {index}",
                FolderPath = Path.Combine(clientA.Fixture.Root, $"p{index}"),
                CreateFolderIfMissing = true,
            });
        }

        var push = await clientA.Sync.RunAsync();

        Assert.True(push.Succeeded, push.Message);
        Assert.Equal(3, push.Pushed);
        Assert.Equal(3, server.Count("SELECT count(*) FROM liftup.entities"));
        Assert.Equal(1, server.Count("SELECT schema_version FROM liftup.info"));

        var pull = await clientB.Sync.RunAsync();

        Assert.True(pull.Succeeded, pull.Message);
        Assert.Equal(3, pull.Pulled);
        Assert.Equal(3, clientB.CountProjects());
    }

    [PostgresFact]
    public async Task Aenderung_von_b_kommt_bei_a_an()
    {
        using var server = new PostgresServer();
        using var clientA = new Client(server.Settings);
        using var clientB = new Client(server.Settings);

        var project = clientA.Projects.Create(new ProjectInput
        {
            Name = "Ausgangsstand",
            FolderPath = Path.Combine(clientA.Fixture.Root, "gemeinsam"),
            CreateFolderIfMissing = true,
        });

        await clientA.Sync.RunAsync();
        await clientB.Sync.RunAsync();

        clientB.Projects.Update(project.Id, new ProjectInput
        {
            Name = "Von B geändert",
            FolderPath = project.FolderPath,
        });

        var fromB = await clientB.Sync.RunAsync();
        Assert.True(fromB.Succeeded, fromB.Message);
        Assert.Equal(1, fromB.Pushed);

        var toA = await clientA.Sync.RunAsync();
        Assert.True(toA.Succeeded, toA.Message);
        Assert.Equal(1, toA.Pulled);
        Assert.Equal("Von B geändert", clientA.ProjectName(project.Id));
        Assert.Equal("Von B geändert", clientB.ProjectName(project.Id));
    }

    [PostgresFact]
    public async Task Gleichzeitige_aenderung_wird_auf_einen_stand_gebracht()
    {
        using var server = new PostgresServer();
        using var clientA = new Client(server.Settings);
        using var clientB = new Client(server.Settings);

        var project = clientA.Projects.Create(new ProjectInput
        {
            Name = "Gemeinsam",
            FolderPath = Path.Combine(clientA.Fixture.Root, "konflikt"),
            CreateFolderIfMissing = true,
        });
        await clientA.Sync.RunAsync();
        await clientB.Sync.RunAsync();

        // Beide ändern denselben Satz, ohne zwischendurch abzugleichen:
        // beide Arbeitsplätze stehen danach auf RowVersion 2.
        clientA.Projects.Update(project.Id, new ProjectInput
        {
            Name = "Von A",
            FolderPath = project.FolderPath,
        });
        clientB.Projects.Update(project.Id, new ProjectInput
        {
            Name = "Von B",
            FolderPath = project.FolderPath,
        });

        var pushA = await clientA.Sync.RunAsync();
        Assert.True(pushA.Succeeded, pushA.Message);
        Assert.Equal(1, pushA.Pushed);

        var pushB = await clientB.Sync.RunAsync();
        Assert.True(pushB.Succeeded, pushB.Message);
        Assert.Equal(0, pushB.Pushed);

        // Beide halten jetzt denselben Satz, und der Server ebenso.
        Assert.Equal("Von A", clientA.ProjectName(project.Id));
        Assert.Equal("Von A", clientB.ProjectName(project.Id));
        Assert.Equal("Von A", server.ProjectName(project.Id));
        Assert.Equal(0, clientB.Sync.PendingCount);
    }

    [PostgresFact]
    public async Task Erneutes_senden_nach_absturz_erzeugt_keine_doppelten_eintraege()
    {
        using var server = new PostgresServer();
        using var client = new Client(server.Settings);

        var project = client.Projects.Create(new ProjectInput
        {
            Name = "Einmalig",
            FolderPath = Path.Combine(client.Fixture.Root, "einmalig"),
            CreateFolderIfMissing = true,
        });

        var first = await client.Sync.RunAsync();
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(1, first.Pushed);

        var entities = server.Count("SELECT count(*) FROM liftup.entities");
        var entries = server.Count("SELECT count(*) FROM liftup.change_log");

        // Absturz nach dem Senden, aber vor der Bestätigung: die Einträge gelten
        // lokal wieder als offen.
        client.ResetSynced();

        var second = await client.Sync.RunAsync();

        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(0, second.Pushed);
        Assert.Equal(entities, server.Count("SELECT count(*) FROM liftup.entities"));
        Assert.Equal(entries, server.Count("SELECT count(*) FROM liftup.change_log"));
        Assert.Equal(0, client.Sync.PendingCount);
        Assert.Equal(1, client.CountProjects());
    }

    /// <summary>Ein Arbeitsplatz: eigene SQLite-Datei, eigenes Gerätekennzeichen, eigener Abgleich.</summary>
    private sealed class Client : IDisposable
    {
        public Client(DatabaseServerSettings settings)
        {
            Fixture = new TestDatabase();
            Session = Fixture.LoginAsAdmin();
            Projects = new ProjectService(Fixture.Database, Session);
            Sync = new SyncService(
                Fixture.Database,
                new PostgresSyncTransport(Fixture.Database, settings),
                new SyncOptions { RetryDelay = TimeSpan.Zero });
        }

        public TestDatabase Fixture { get; }

        public Session Session { get; }

        public ProjectService Projects { get; }

        public SyncService Sync { get; }

        public string? ProjectName(Guid id)
        {
            using var connection = Fixture.Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Name FROM Projects WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            return command.ExecuteScalar() as string;
        }

        public int CountProjects()
        {
            using var connection = Fixture.Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM Projects;";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>Simuliert einen Absturz zwischen Senden und Bestätigen.</summary>
        public void ResetSynced()
        {
            using var connection = Fixture.Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ChangeLog SET SyncedUtc = NULL;";
            command.ExecuteNonQuery();
        }

        public void Dispose() => Fixture.Dispose();
    }

    /// <summary>Wegwerf-Datenbank und -Rolle für einen Test.</summary>
    private sealed class PostgresServer : IDisposable
    {
        private const string ClientUser = "liftup_test";

        public PostgresServer()
        {
            var admin = new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("LIFTUP_TEST_POSTGRES")!);

            Admin = new DatabaseServerSettings
            {
                Host = admin.Host ?? "127.0.0.1",
                Port = admin.Port,
                Database = admin.Database ?? "postgres",
                User = admin.Username!,
                Password = admin.Password ?? string.Empty,
                SslMode = "Prefer",
            };

            DatabaseName = "liftup_test_" + Guid.NewGuid().ToString("N")[..12];
            Settings = new DatabaseServerSettings
            {
                Host = admin.Host ?? "127.0.0.1",
                Port = admin.Port,
                Database = DatabaseName,
                User = ClientUser,
                Password = "Geheim123!",
                SslMode = "Prefer",
            };

            var result = new ServerProvisioner(Admin, Settings).ProvisionAsync().GetAwaiter().GetResult();
            Assert.True(result.DatabaseCreated, "Die Testdatenbank wurde nicht angelegt.");
        }

        public DatabaseServerSettings Admin { get; }

        public DatabaseServerSettings Settings { get; }

        public string DatabaseName { get; }

        public long Count(string sql)
        {
            using var connection = new NpgsqlConnection(Settings.BuildConnectionString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public string? ProjectName(Guid id)
        {
            using var connection = new NpgsqlConnection(Settings.BuildConnectionString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT data ->> 'Name' FROM liftup.entities
                WHERE entity_type = 'Project' AND entity_id = @id;
                """;
            command.Parameters.AddWithValue("id", id.ToString("D"));
            return command.ExecuteScalar() as string;
        }

        public void Dispose()
        {
            // Offene Verbindungen aus dem Pool schließen, sonst lässt sich die
            // Datenbank nicht entfernen.
            NpgsqlConnection.ClearAllPools();

            try
            {
                using var connection = new NpgsqlConnection(Admin.BuildConnectionString());
                connection.Open();

                using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE);";
                drop.ExecuteNonQuery();

                using var role = connection.CreateCommand();
                role.CommandText = $"DROP ROLE IF EXISTS \"{ClientUser}\";";
                role.ExecuteNonQuery();
            }
            catch (PostgresException)
            {
                // Aufräumen ist für den Testausgang nicht entscheidend.
            }
        }
    }
}

/// <summary>Test, der einen echten PostgreSQL-Server voraussetzt.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIFTUP_TEST_POSTGRES")))
        {
            Skip = "Kein PostgreSQL-Server angegeben; starten mit tools/postgres-sync-test.sh.";
        }
    }
}
