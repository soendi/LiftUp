using System.Globalization;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using LiftUp.Core.Sync;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class DatabaseSyncTests
{
    private const string VersionList = """
        [
          { "major": "15", "latestMinor": "12", "supported": true, "current": false },
          { "major": "16", "latestMinor": "8", "supported": true, "current": false },
          { "major": "17", "latestMinor": "4", "supported": true, "current": false },
          { "major": "18", "latestMinor": "1", "supported": true, "current": true },
          { "major": "9.6", "latestMinor": "24", "supported": false, "current": false }
        ]
        """;

    [Fact]
    public void Verbindungsdatei_wird_gespeichert_und_gelesen()
    {
        var path = Path.Combine(Path.GetTempPath(), "liftup-tests", Guid.NewGuid().ToString("N"),
            ConnectionFile.DefaultFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            var settings = new DatabaseServerSettings
            {
                Host = "db-server",
                Port = 5433,
                Database = "liftup",
                User = "liftup",
                Password = "Geheim123!",
                SslMode = "Require",
            };

            ConnectionFile.From(settings, "18.1").Save(path);
            var loaded = ConnectionFile.Load(path);

            Assert.Equal("db-server", loaded.Host);
            Assert.Equal(5433, loaded.Port);
            Assert.Equal("Geheim123!", loaded.Password);
            Assert.Equal("Require", loaded.SslMode);
            Assert.Equal("18.1", loaded.ServerVersion);
            Assert.True(loaded.ToSettings().IsComplete);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Fremde_datei_wird_abgelehnt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"liftup-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "irgendwas": 1 }""");

        try
        {
            var exception = Assert.Throws<ValidationException>(() => ConnectionFile.Load(path));
            Assert.Contains("Zugangsdaten", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unvollstaendige_einstellungen_werden_gemeldet()
    {
        var settings = new DatabaseServerSettings { Host = "", Port = 0, Database = "", User = "" };

        var errors = settings.Validate();

        Assert.Contains(errors, message => message.Contains("Server"));
        Assert.Contains(errors, message => message.Contains("Port"));
        Assert.Contains(errors, message => message.Contains("Datenbank"));
        Assert.Contains(errors, message => message.Contains("Benutzername"));
    }

    [Fact]
    public void Neueste_postgresql_version_wird_gewaehlt()
    {
        var release = PostgresReleaseService.Latest(VersionList);

        Assert.Equal(18, release.Major);
        Assert.Equal("18.1", release.Version);
        Assert.True(release.Current);
        Assert.Equal("PostgreSQL.PostgreSQL.18", PostgresReleaseService.WingetPackageId(release));
    }

    [Fact]
    public void Dokumente_und_bilder_bleiben_lokal()
    {
        Assert.True(SyncTableMap.TryGetTable("Project", out var table));
        Assert.Equal("Projects", table);

        Assert.False(SyncTableMap.TryGetTable("Document", out _));
        Assert.Contains("PDF", SyncTableMap.SkipReason("Document"));
        Assert.Contains("Bilddatei", SyncTableMap.SkipReason("Image"));
    }

    [Fact]
    public void Einstellungen_werden_mit_geschuetztem_passwort_gespeichert()
    {
        using var fixture = new TestDatabase();
        var store = new SyncSettingsStore(fixture.Database);

        store.Save(new DatabaseServerSettings
        {
            Host = "server-01",
            Port = 5432,
            Database = "liftup",
            User = "liftup",
            Password = "Geheim123!",
        });
        store.SetLastChangeId(4711);
        store.SetLastResult(new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc), true, "Abgleich: 2 gesendet.");

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("server-01", loaded!.Host);
        Assert.Equal("Geheim123!", loaded.Password);
        Assert.Equal(4711, store.LastChangeId);
        Assert.True(store.LastRunSucceeded);
        Assert.Contains("2 gesendet", store.LastMessage);
        Assert.DoesNotContain("Geheim123!", fixture.Database.GetSetting("Sync.Password") ?? string.Empty);
    }

    [Fact]
    public void Aelterer_serverstand_ueberschreibt_nichts()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var project = new ProjectService(fixture.Database, admin).Create(new ProjectInput
        {
            Name = "Lokal neuer",
            FolderPath = Path.Combine(fixture.Root, "lokal"),
            CreateFolderIfMissing = true,
        });

        var older = new RemoteChange(
            1,
            "Project",
            project.Id.ToString("D"),
            $"{{\"Id\":\"{project.Id:D}\",\"Name\":\"Veraltet\",\"RowVersion\":1}}",
            1,
            false,
            DateTime.UtcNow.AddMinutes(-5),
            "anderer-arbeitsplatz");

        var result = LocalChangeApplier.Apply(fixture.Database, [older]);

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);

        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Projects WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", project.Id.ToString("D"));
        Assert.Equal("Lokal neuer", command.ExecuteScalar() as string);
    }

    [Fact]
    public void Neuer_serverdatensatz_wird_angelegt()
    {
        using var fixture = new TestDatabase();
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var change = new RemoteChange(
            1,
            "Project",
            "11111111-1111-1111-1111-111111111111",
            $$"""
              {
                "Id": "11111111-1111-1111-1111-111111111111",
                "Name": "Vom Server",
                "Number": "2026-0007",
                "FolderPath": "C:\\Projekte\\2026-0007",
                "Status": 1,
                "CreatedUtc": "{{now}}",
                "UpdatedUtc": "{{now}}",
                "RowVersion": 1,
                "IsDeleted": 0
              }
              """,
            1,
            false,
            DateTime.UtcNow,
            "anderer-arbeitsplatz");

        var result = LocalChangeApplier.Apply(fixture.Database, [change]);

        Assert.Equal(1, result.Applied);
        Assert.Empty(result.Errors);

        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Projects WHERE Id = '11111111-1111-1111-1111-111111111111';";
        Assert.Equal("Vom Server", command.ExecuteScalar() as string);
    }
}
