using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using LiftUp.Core.Sync;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class SyncServiceTests
{
    private sealed class FakeTransport : ISyncTransport
    {
        public List<EntitySnapshot> Pushed { get; } = [];
        public List<RemoteChange> Incoming { get; } = [];

        public string Name => "Testziel";
        public bool IsConfigured => true;

        public Task<PushResult> PushAsync(IReadOnlyList<EntitySnapshot> snapshots, CancellationToken cancellationToken)
        {
            Pushed.AddRange(snapshots);
            return Task.FromResult(new PushResult(snapshots.Count, []));
        }

        public Task<IReadOnlyList<RemoteChange>> PullAsync(long afterChangeId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteChange>>(
                Incoming.Where(change => change.ChangeId > afterChangeId).ToList());
    }

    [Fact]
    public async Task Ohne_ziel_bleiben_daten_lokal()
    {
        using var fixture = new TestDatabase();
        var sync = new SyncService(fixture.Database, new NotConfiguredSyncTransport());

        var result = await sync.RunAsync();

        Assert.False(result.Attempted);
        Assert.Contains("Kein PostgreSQL-Ziel", result.Message);
    }

    [Fact]
    public async Task Aenderungen_werden_uebertragen_und_bestaetigt()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var service = new ProjectService(fixture.Database, admin);
        service.Create(new ProjectInput
        {
            Name = "Sync-Test",
            FolderPath = Path.Combine(fixture.Root, "sync"),
            CreateFolderIfMissing = true,
        });

        var transport = new FakeTransport();
        var sync = new SyncService(fixture.Database, transport);

        var first = await sync.RunAsync();
        Assert.True(first.Attempted);
        Assert.True(first.Pushed > 0);
        Assert.NotEmpty(transport.Pushed);

        var second = await sync.RunAsync();
        Assert.Equal(0, second.Pushed);
        Assert.Contains("bereits abgeglichen", second.Message);
    }

    [Fact]
    public async Task Hereinkommende_aenderung_wird_uebernommen()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var project = new ProjectService(fixture.Database, admin).Create(new ProjectInput
        {
            Name = "Alt",
            FolderPath = Path.Combine(fixture.Root, "remote"),
            CreateFolderIfMissing = true,
        });

        var transport = new FakeTransport();
        transport.Incoming.Add(new RemoteChange(
            7,
            "Project",
            project.Id.ToString("D"),
            $"{{\"Id\":\"{project.Id:D}\",\"Name\":\"Neu vom Server\",\"RowVersion\":2}}",
            2,
            false,
            DateTime.UtcNow,
            "anderer-arbeitsplatz"));

        var sync = new SyncService(fixture.Database, transport);
        var result = await sync.RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Pulled);

        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Projects WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", project.Id.ToString("D"));
        Assert.Equal("Neu vom Server", command.ExecuteScalar() as string);
    }
}
