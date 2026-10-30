using LiftUp.Core.Data;

namespace LiftUp.Core.Sync;

/// <summary>Eine Änderung, die von einem anderen Arbeitsplatz hereinkommt.</summary>
public sealed record RemoteChange(
    string EntityType,
    string EntityId,
    string Operation,
    string Payload,
    long RowVersion,
    DateTime ChangedUtc,
    string OriginDeviceId);

/// <summary>
/// Transport zum zentralen PostgreSQL-Server. Die Implementierung wird später
/// ergänzt (z. B. REST-Endpunkt oder direkter Npgsql-Zugriff); alles andere im
/// Programm hängt nur von dieser Schnittstelle ab.
/// </summary>
public interface ISyncTransport
{
    string Name { get; }
    bool IsConfigured { get; }

    Task PushAsync(IReadOnlyList<ChangeLogEntry> changes, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteChange>> PullAsync(DateTime sinceUtc, CancellationToken cancellationToken);
}

/// <summary>Standard, solange kein Server eingerichtet ist: alles bleibt lokal.</summary>
public sealed class NotConfiguredSyncTransport : ISyncTransport
{
    public string Name => "nicht eingerichtet";
    public bool IsConfigured => false;

    public Task PushAsync(IReadOnlyList<ChangeLogEntry> changes, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Es ist kein Synchronisationsziel eingerichtet.");

    public Task<IReadOnlyList<RemoteChange>> PullAsync(DateTime sinceUtc, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Es ist kein Synchronisationsziel eingerichtet.");
}

public sealed record SyncResult(bool Attempted, int Pushed, int Pending, string Message);

/// <summary>
/// Überträgt die offenen Einträge aus dem Änderungsprotokoll und bestätigt sie
/// erst danach. Der Abgleich selbst (Konflikte, Feldzusammenführung) gehört in
/// den Transport, damit die Regeln zentral an einer Stelle liegen.
/// </summary>
public sealed class SyncService(Database database, ISyncTransport transport)
{
    private readonly ChangeLogWriter _changes = new(database);

    public bool IsConfigured => transport.IsConfigured;
    public string TransportName => transport.Name;

    public async Task<SyncResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!transport.IsConfigured)
        {
            return new SyncResult(false, 0, _changes.GetPending(1).Count,
                "Es ist kein PostgreSQL-Ziel eingerichtet. Die Daten bleiben lokal und werden später übertragen.");
        }

        var pending = _changes.GetPending();
        if (pending.Count == 0)
        {
            return new SyncResult(true, 0, 0, "Alles ist bereits übertragen.");
        }

        await transport.PushAsync(pending, cancellationToken).ConfigureAwait(false);
        _changes.MarkSynced(pending.Select(entry => entry.Id).ToArray());

        return new SyncResult(true, pending.Count, 0, $"{pending.Count} Änderung(en) übertragen.");
    }
}
