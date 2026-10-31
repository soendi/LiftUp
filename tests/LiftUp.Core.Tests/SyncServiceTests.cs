using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using LiftUp.Core.Sync;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class SyncServiceTests
{
    private sealed class FakeTransport(bool failPush = false) : ISyncTransport
    {
        public List<ChangeLogEntry> Pushed { get; } = [];

        /// <summary>Was auf dem Server liegt; die Pull-Abfrage filtert nach der Nummer.</summary>
        public List<RemoteChange> Remote { get; } = [];

        public string Name => "Testziel";
        public string ScopeKey => "test";
        public bool IsConfigured => true;

        public Task PushAsync(IReadOnlyList<ChangeLogEntry> changes, CancellationToken cancellationToken)
        {
            if (failPush)
            {
                throw new SyncException("Der Server ist nicht erreichbar.");
            }

            Pushed.AddRange(changes);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RemoteChange>> PullAsync(long sinceServerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteChange>>(
                Remote.Where(change => change.ServerId > sinceServerId).ToList());
    }

    private static RemoteChange RemoteEntry(long serverId, string entityId, string origin) => new(
        serverId,
        "Project",
        entityId,
        "Update",
        """{"Name":"Vom anderen Arbeitsplatz"}""",
        2,
        DateTime.UtcNow,
        origin,
        "Erika");

    [Fact]
    public async Task Ohne_ziel_bleiben_daten_lokal()
    {
        using var fixture = new TestDatabase();
        var sync = new SyncService(fixture.Database, new NotConfiguredSyncTransport());

        var result = await sync.RunAsync();

        Assert.False(result.Attempted);
        Assert.Contains("kein PostgreSQL-Ziel", result.Message);
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
        Assert.Equal(0, first.Pending);
        Assert.NotEmpty(transport.Pushed);

        var second = await sync.RunAsync();
        Assert.Equal(0, second.Pushed);
        Assert.Contains("bereits übertragen", second.Message);
    }

    [Fact]
    public async Task Uebernimmt_eintraege_anderer_arbeitsplaetze()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var transport = new FakeTransport();
        transport.Remote.Add(RemoteEntry(1, Guid.NewGuid().ToString("D"), "pc-2"));
        transport.Remote.Add(RemoteEntry(2, Guid.NewGuid().ToString("D"), "pc-2"));
        transport.Remote.Add(RemoteEntry(3, Guid.NewGuid().ToString("D"), "pc-2"));

        var sync = new SyncService(fixture.Database, transport);
        var result = await sync.RunAsync();

        Assert.True(result.Attempted);
        Assert.Equal(3, result.Pulled);
        Assert.Equal(3, sync.GetWatermark());
        Assert.NotNull(sync.GetLastRunUtc());

        var log = new ChangeLogService(fixture.Database, admin).Query(new ChangeLogFilter());
        var incoming = log.Where(entry => entry.OriginDeviceId == "pc-2").ToList();
        Assert.Equal(3, incoming.Count);
        Assert.All(incoming, entry => Assert.True(entry.IsSynced));
        Assert.All(incoming, entry => Assert.Equal("Erika", entry.Actor));

        // Der Lesestand verhindert, dass dieselben Einträge erneut ankommen.
        var second = await sync.RunAsync();
        Assert.Equal(0, second.Pulled);
        Assert.Contains("bereits übertragen", second.Message);
    }

    [Fact]
    public async Task Uebernommene_eintraege_gehen_nicht_wieder_hinauf()
    {
        using var fixture = new TestDatabase();
        fixture.LoginAsAdmin();
        var transport = new FakeTransport();
        transport.Remote.Add(RemoteEntry(1, Guid.NewGuid().ToString("D"), "pc-3"));
        var sync = new SyncService(fixture.Database, transport);

        await sync.RunAsync();
        Assert.Empty(transport.Pushed);

        var second = await sync.RunAsync();
        Assert.Empty(transport.Pushed);
        Assert.Equal(0, second.Pushed);
    }

    [Fact]
    public async Task Fehler_laesst_eintraege_offen()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        new ProjectService(fixture.Database, admin).Create(new ProjectInput
        {
            Name = "Sync-Fehler",
            FolderPath = Path.Combine(fixture.Root, "fehler"),
            CreateFolderIfMissing = true,
        });

        var sync = new SyncService(fixture.Database, new FakeTransport(failPush: true));
        var result = await sync.RunAsync();

        Assert.True(result.Attempted);
        Assert.Equal(0, result.Pushed);
        Assert.True(result.Pending > 0);
        Assert.Contains("unterbrochen", result.Message);
        Assert.Contains("nicht erreichbar", result.Message);
    }
}
