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
        public List<ChangeLogEntry> Pushed { get; } = [];

        public string Name => "Testziel";
        public bool IsConfigured => true;

        public Task PushAsync(IReadOnlyList<ChangeLogEntry> changes, CancellationToken cancellationToken)
        {
            Pushed.AddRange(changes);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RemoteChange>> PullAsync(DateTime sinceUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteChange>>([]);
    }

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
        Assert.NotEmpty(transport.Pushed);

        var second = await sync.RunAsync();
        Assert.Equal(0, second.Pushed);
        Assert.Contains("bereits übertragen", second.Message);
    }
}
