using System.Globalization;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using LiftUp.Core.Sync;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Verhalten bei mehreren gleichzeitig geänderten Datensätzen und bei
/// Verbindungsabbrüchen: Pakete, Seiten, gesicherter Fortschritt, Konflikte.
/// </summary>
public sealed class SyncReliabilityTests
{
    /// <summary>Transport, der sich je Test steuern lässt (Fehler, Konflikte, Seiten).</summary>
    private sealed class ScriptedTransport : ISyncTransport
    {
        public List<IReadOnlyList<EntitySnapshot>> PushCalls { get; } = [];

        public List<long> PullCursors { get; } = [];

        public List<EntitySnapshot> Conflicts { get; } = [];

        public List<RemoteChange> Changes { get; } = [];

        public Queue<Exception?> PushFailures { get; } = new();

        public Queue<Exception?> PullFailures { get; } = new();

        public int PageSize { get; set; } = 1000;

        public string Name => "Testziel";

        public bool IsConfigured => true;

        public Task<PushResult> PushAsync(IReadOnlyList<EntitySnapshot> snapshots, CancellationToken cancellationToken)
        {
            PushCalls.Add(snapshots);

            if (PushFailures.Count > 0 && PushFailures.Dequeue() is { } failure)
            {
                throw failure;
            }

            // Sätze, die als Konflikt zurückkommen, hat der Server nicht übernommen.
            var accepted = Math.Max(0, snapshots.Count - Conflicts.Count);
            return Task.FromResult(new PushResult(accepted, Conflicts));
        }

        public Task<IReadOnlyList<RemoteChange>> PullAsync(long afterChangeId, CancellationToken cancellationToken)
        {
            PullCursors.Add(afterChangeId);

            if (PullFailures.Count > 0 && PullFailures.Dequeue() is { } failure)
            {
                throw failure;
            }

            return Task.FromResult<IReadOnlyList<RemoteChange>>(
                Changes.Where(change => change.ChangeId > afterChangeId).Take(PageSize).ToList());
        }
    }

    [Fact]
    public async Task Mehrere_gleichzeitig_geaenderte_datensaetze_gehen_in_einem_paket_raus()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        for (var index = 1; index <= 5; index++)
        {
            service.Create(new ProjectInput
            {
                Name = $"Projekt {index}",
                FolderPath = Path.Combine(fixture.Root, $"p{index}"),
                CreateFolderIfMissing = true,
            });
        }

        var transport = new ScriptedTransport();
        var sync = new SyncService(fixture.Database, transport);

        var result = await sync.RunAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(5, result.Pushed);
        Assert.Single(transport.PushCalls);
        Assert.Equal(5, transport.PushCalls[0].Count);
        Assert.Equal(0, sync.PendingCount);
    }

    [Fact]
    public async Task Zwei_aenderungen_am_selben_datensatz_ergeben_einen_snapshot()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var project = service.Create(new ProjectInput
        {
            Name = "Erster Name",
            FolderPath = Path.Combine(fixture.Root, "doppelt"),
            CreateFolderIfMissing = true,
        });
        service.Update(project.Id, new ProjectInput
        {
            Name = "Zweiter Name",
            FolderPath = project.FolderPath,
        });

        var transport = new ScriptedTransport();
        var sync = new SyncService(fixture.Database, transport);

        var result = await sync.RunAsync();

        Assert.True(result.Succeeded, result.Message);
        var snapshot = Assert.Single(transport.PushCalls[0]);
        Assert.Contains("Zweiter Name", snapshot.Data);
        Assert.Equal(0, sync.PendingCount);
    }

    [Fact]
    public async Task Abbruch_beim_senden_bestaetigt_nur_die_fertigen_pakete()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        for (var index = 1; index <= 5; index++)
        {
            service.Create(new ProjectInput
            {
                Name = $"Projekt {index}",
                FolderPath = Path.Combine(fixture.Root, $"p{index}"),
                CreateFolderIfMissing = true,
            });
        }

        var transport = new ScriptedTransport();
        transport.PushFailures.Enqueue(null);
        transport.PushFailures.Enqueue(new TimeoutException("Leitung weg"));

        var sync = new SyncService(fixture.Database, transport, new SyncOptions
        {
            PushChunkSize = 2,
            MaxAttempts = 1,
        });

        var first = await sync.RunAsync();

        Assert.False(first.Succeeded);
        Assert.Equal(2, transport.PushCalls[0].Count);
        Assert.Equal(3, sync.PendingCount);

        var second = await sync.RunAsync();

        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(3, second.Pushed);
        Assert.Equal(2, transport.PushCalls[2].Count);
        Assert.Single(transport.PushCalls[3]);
        Assert.Equal(0, sync.PendingCount);
    }

    [Fact]
    public async Task Verbindungsabbruch_wird_wiederholt()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        service.Create(new ProjectInput
        {
            Name = "Wackelig",
            FolderPath = Path.Combine(fixture.Root, "wackelig"),
            CreateFolderIfMissing = true,
        });

        var transport = new ScriptedTransport();
        transport.PushFailures.Enqueue(new TimeoutException("Leitung weg"));
        transport.PushFailures.Enqueue(new TimeoutException("Leitung weg"));

        var sync = new SyncService(fixture.Database, transport, new SyncOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.Zero,
        });

        var result = await sync.RunAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(3, transport.PushCalls.Count);
        Assert.Contains("Verbindungsstörung", result.Message);
        Assert.Equal(0, sync.PendingCount);
    }

    [Fact]
    public async Task Dauerhafte_stoerung_meldet_fehler_und_behaelt_offene_aenderungen()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        service.Create(new ProjectInput
        {
            Name = "Bleibt offen",
            FolderPath = Path.Combine(fixture.Root, "offen"),
            CreateFolderIfMissing = true,
        });

        var transport = new ScriptedTransport();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            transport.PushFailures.Enqueue(new TimeoutException("Server nicht erreichbar"));
        }

        var sync = new SyncService(fixture.Database, transport, new SyncOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.Zero,
        });

        var result = await sync.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Synchronisierung nicht möglich", result.Message);
        Assert.Equal(3, transport.PushCalls.Count);
        Assert.Equal(1, sync.PendingCount);
    }

    [Fact]
    public async Task Grosser_rueckstand_wird_seitenweise_geholt()
    {
        using var fixture = new TestDatabase();
        var transport = new ScriptedTransport { PageSize = 2 };
        for (var index = 1; index <= 5; index++)
        {
            transport.Changes.Add(Change(index, Guid.NewGuid(), $"Server {index}", index));
        }

        var sync = new SyncService(fixture.Database, transport, new SyncOptions { PullPageSize = 2 });

        var result = await sync.RunAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(5, result.Pulled);
        Assert.Equal(new long[] { 0, 2, 4 }, transport.PullCursors);
        Assert.Equal(5, new SyncSettingsStore(fixture.Database).LastChangeId);
        Assert.Equal(5, CountProjects(fixture.Database));
    }

    [Fact]
    public async Task Abbruch_beim_holen_behaelt_die_fertige_seite()
    {
        using var fixture = new TestDatabase();
        var transport = new ScriptedTransport { PageSize = 2 };
        for (var index = 1; index <= 5; index++)
        {
            transport.Changes.Add(Change(index, Guid.NewGuid(), $"Server {index}", index));
        }

        transport.PullFailures.Enqueue(null);
        transport.PullFailures.Enqueue(new TimeoutException("Leitung weg"));

        var sync = new SyncService(fixture.Database, transport, new SyncOptions
        {
            PullPageSize = 2,
            MaxAttempts = 1,
        });

        var first = await sync.RunAsync();

        Assert.False(first.Succeeded);
        Assert.Equal(2, new SyncSettingsStore(fixture.Database).LastChangeId);
        Assert.Equal(2, CountProjects(fixture.Database));

        var second = await sync.RunAsync();

        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(2, transport.PullCursors[transport.PullCursors.Count - 2]);
        Assert.Equal(3, second.Pulled);
        Assert.Equal(5, new SyncSettingsStore(fixture.Database).LastChangeId);
        Assert.Equal(5, CountProjects(fixture.Database));
    }

    [Fact]
    public async Task Konflikt_wird_mit_dem_serverstand_aufgeloest()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var project = service.Create(new ProjectInput
        {
            Name = "Erster Stand",
            FolderPath = Path.Combine(fixture.Root, "konflikt"),
            CreateFolderIfMissing = true,
        });

        // Beide Arbeitsplätze ändern denselben Satz: lokal entsteht Version 2 …
        BumpLocally(fixture.Database, project.Id, "Lokal geändert");

        // … und der Server hat ebenfalls Version 2, aber mit anderem Inhalt.
        var transport = new ScriptedTransport();
        transport.Conflicts.Add(new EntitySnapshot(
            "Project",
            project.Id.ToString("D"),
            $$"""
              {"Id":"{{project.Id:D}}","Name":"Server gewinnt","RowVersion":2}
              """,
            2,
            false,
            DateTime.UtcNow,
            "anderer-arbeitsplatz"));

        var sync = new SyncService(fixture.Database, transport);
        var result = await sync.RunAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(0, result.Pushed);
        Assert.Equal("Server gewinnt", ReadProjectName(fixture.Database, project.Id));
        Assert.Equal(0, sync.PendingCount);
    }

    [Fact]
    public void Neuerer_lokaler_stand_bleibt_beim_konflikt_stehen()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var project = service.Create(new ProjectInput
        {
            Name = "Lokal",
            FolderPath = Path.Combine(fixture.Root, "lokal-neuer"),
            CreateFolderIfMissing = true,
        });
        BumpLocally(fixture.Database, project.Id, "Lokal neuer");

        var older = new EntitySnapshot(
            "Project",
            project.Id.ToString("D"),
            $$"""
              {"Id":"{{project.Id:D}}","Name":"Server alt","RowVersion":1}
              """,
            1,
            false,
            DateTime.UtcNow,
            "anderer-arbeitsplatz");

        var result = LocalChangeApplier.ApplyServerState(fixture.Database, [older]);

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("Lokal neuer", ReadProjectName(fixture.Database, project.Id));
    }

    [Fact]
    public async Task Fehlerhafter_datensatz_blockiert_die_uebrigen_nicht()
    {
        using var fixture = new TestDatabase();
        var transport = new ScriptedTransport();
        transport.Changes.Add(new RemoteChange(
            1,
            "Project",
            Guid.NewGuid().ToString("D"),
            "{kaputt",
            1,
            false,
            DateTime.UtcNow,
            "anderer-arbeitsplatz"));
        transport.Changes.Add(Change(2, Guid.NewGuid(), "Guter Satz", 1));

        var sync = new SyncService(fixture.Database, transport);
        var result = await sync.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Pulled);
        Assert.Equal(1, result.Failed);
        Assert.Contains("fehlgeschlagen", result.Message);
        Assert.Equal(2, new SyncSettingsStore(fixture.Database).LastChangeId);
        Assert.Equal(1, CountProjects(fixture.Database));
    }

    private static RemoteChange Change(long changeId, Guid entityId, string name, long rowVersion) => new(
        changeId,
        "Project",
        entityId.ToString("D"),
        $$"""
          {
            "Id": "{{entityId:D}}",
            "Name": "{{name}}",
            "FolderPath": "C:\\Sync\\{{entityId:D}}",
            "Status": 0,
            "CreatedUtc": "2026-01-01T00:00:00.0000000Z",
            "UpdatedUtc": "2026-01-01T00:00:00.0000000Z",
            "RowVersion": {{rowVersion}},
            "IsDeleted": 0
          }
          """,
        rowVersion,
        false,
        DateTime.UtcNow,
        "anderer-arbeitsplatz");

    private static void BumpLocally(Database database, Guid id, string name)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Projects SET Name = $name, RowVersion = RowVersion + 1 WHERE Id = $id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static string? ReadProjectName(Database database, Guid id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Projects WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return command.ExecuteScalar() as string;
    }

    private static int CountProjects(Database database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM Projects;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
