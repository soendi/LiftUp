using LiftUp.Core.Backup;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class BackupTests
{
    private static ProjectInput Input(TestDatabase fixture, string name, string folder) => new()
    {
        Name = name,
        FolderPath = Path.Combine(fixture.Root, folder),
        CreateFolderIfMissing = true,
    };

    private static long CountProjects(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM Projects WHERE IsDeleted = 0;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public void Sicherung_ist_eine_konsistente_kopie()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        new ProjectService(fixture.Database, session).Create(Input(fixture, "Erstes", "p1"));

        var backups = new BackupService(fixture.Database, Path.Combine(fixture.Root, "backups"));
        var file = backups.Create("test");

        Assert.True(File.Exists(file.FilePath));
        Assert.True(file.SizeBytes > 0);
        Assert.Equal(1, CountProjects(file.FilePath));
        Assert.Single(backups.List());
    }

    [Fact]
    public void Wiederherstellen_holt_geloeschte_daten_zurueck()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var project = projects.Create(Input(fixture, "Wichtig", "p1"));

        var backups = new BackupService(fixture.Database, Path.Combine(fixture.Root, "backups"));
        var file = backups.Create("vor-löschung");

        projects.Delete(project.Id, moveFolderToRecycleBin: false);
        Assert.Empty(projects.Search(new ProjectQuery()));

        var safety = backups.Restore(file.FilePath);

        Assert.True(File.Exists(safety.FilePath));
        Assert.Single(projects.Search(new ProjectQuery()));
    }

    [Fact]
    public void Alte_sicherungen_werden_entfernt_die_neueste_bleibt()
    {
        using var fixture = new TestDatabase();
        var backups = new BackupService(fixture.Database, Path.Combine(fixture.Root, "backups"));

        var old = backups.Create("alt");
        var fresh = backups.Create("neu");

        File.SetLastWriteTimeUtc(old.FilePath, DateTime.UtcNow.AddDays(-45));

        var removed = backups.Prune(keepDays: 30);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old.FilePath));
        Assert.True(File.Exists(fresh.FilePath));
    }

    [Fact]
    public void Eine_einzige_alte_sicherung_bleibt_erhalten()
    {
        using var fixture = new TestDatabase();
        var backups = new BackupService(fixture.Database, Path.Combine(fixture.Root, "backups"));

        var only = backups.Create();
        File.SetLastWriteTimeUtc(only.FilePath, DateTime.UtcNow.AddDays(-400));

        Assert.Equal(0, backups.Prune(keepDays: 30));
        Assert.True(File.Exists(only.FilePath));
    }

    [Fact]
    public void Ungueltige_datei_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var backups = new BackupService(fixture.Database, Path.Combine(fixture.Root, "backups"));
        var broken = Path.Combine(fixture.Root, "kaputt.db");
        File.WriteAllText(broken, "das ist keine Datenbank");

        var exception = Assert.Throws<ValidationException>(() => backups.Restore(broken));
        Assert.Contains("keine gültige", exception.Message);
    }

    [Fact]
    public void Optionen_werden_gespeichert_und_gelesen()
    {
        using var fixture = new TestDatabase();
        var backups = new BackupService(fixture.Database, Path.Combine(fixture.Root, "backups"));

        backups.SaveOptions(new BackupOptions
        {
            Target = BackupTargetKind.OneDrive,
            LocalFolder = Path.Combine(fixture.Root, "eigener-ordner"),
            KeepDays = 14,
            UploadToCloud = true,
        });

        var loaded = backups.LoadOptions();

        Assert.Equal(BackupTargetKind.OneDrive, loaded.Target);
        Assert.Equal(14, loaded.KeepDays);
        Assert.True(loaded.UploadToCloud);
        Assert.Equal(Path.Combine(fixture.Root, "eigener-ordner"), backups.ResolveFolder(loaded));
    }

    [Fact]
    public void Ohne_einstellungen_gilt_der_standardordner()
    {
        using var fixture = new TestDatabase();
        var backups = new BackupService(fixture.Database, Path.Combine(fixture.Root, "backups"));

        var options = backups.LoadOptions();

        Assert.Equal(BackupTargetKind.LocalFolder, options.Target);
        Assert.Equal(30, options.KeepDays);
        Assert.Equal(Path.Combine(fixture.Root, "backups"), backups.ResolveFolder(options));
    }
}
