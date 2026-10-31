using System.Net.Sockets;
using LiftUp.Core.Data;
using Npgsql;

namespace LiftUp.Core.Sync;

/// <summary>
/// Transport zum zentralen PostgreSQL-Server. Alles andere im Programm hängt nur
/// von dieser Schnittstelle ab, damit die Anwendung auch ohne Server läuft.
/// </summary>
public interface ISyncTransport
{
    string Name { get; }

    bool IsConfigured { get; }

    /// <summary>
    /// Sendet ein Paket vollständiger Datensätze. Der Server behält den neueren
    /// Stand und gibt die Sätze zurück, die er nicht übernommen hat, damit der
    /// Client seinen Stand daran angleichen kann.
    /// </summary>
    Task<PushResult> PushAsync(IReadOnlyList<EntitySnapshot> snapshots, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteChange>> PullAsync(long afterChangeId, CancellationToken cancellationToken);
}

/// <summary>Antwort des Servers auf ein Sendepaket.</summary>
public sealed record PushResult(int Accepted, IReadOnlyList<EntitySnapshot> Conflicts)
{
    public static PushResult Empty { get; } = new(0, []);
}

/// <summary>Standard, solange kein Server eingerichtet ist: alles bleibt lokal.</summary>
public sealed class NotConfiguredSyncTransport : ISyncTransport
{
    public string Name => "nicht eingerichtet";

    public bool IsConfigured => false;

    public Task<PushResult> PushAsync(IReadOnlyList<EntitySnapshot> snapshots, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Es ist kein Synchronisationsziel eingerichtet.");

    public Task<IReadOnlyList<RemoteChange>> PullAsync(long afterChangeId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Es ist kein Synchronisationsziel eingerichtet.");
}

/// <summary>Feinabstimmung des Abgleichs; die Vorgaben passen für den Alltag.</summary>
public sealed record SyncOptions
{
    /// <summary>So viele Änderungen gehen in einem Paket zum Server.</summary>
    public int PushChunkSize { get; init; } = 200;

    /// <summary>So viele Änderungen holt der Client auf einmal.</summary>
    public int PullPageSize { get; init; } = 1000;

    /// <summary>So oft wird ein durch eine Verbindungsstörung abgebrochener Lauf wiederholt.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Wartezeit vor einer Wiederholung; sie verdoppelt sich mit jedem Versuch.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Obergrenze an Seiten je Lauf, damit ein Lauf nicht endlos dauert.</summary>
    public int MaxPullPages { get; init; } = 50;
}

/// <summary>Ergebnis eines Abgleichs, wie es die Oberfläche anzeigt.</summary>
public sealed record SyncResult(
    bool Attempted,
    int Pushed,
    int Pulled,
    int Failed,
    bool Succeeded,
    string Message);

/// <summary>
/// Führt den Abgleich in beide Richtungen aus: offene lokale Änderungen paketweise
/// senden, danach die seit der letzten Nummer eingetroffenen Serveränderungen
/// seitenweise übernehmen.
///
/// Zuverlässigkeit: Ein Paket gilt erst als bestätigt, wenn der Server es
/// angenommen und ein etwaiger Konflikt lokal geklärt ist. Der Lesestand wird
/// nach jeder Seite gespeichert. Bricht die Verbindung ab, wiederholt der Dienst
/// den Lauf; bereits bestätigte Pakete und Seiten werden nicht erneut übertragen,
/// weil alles serverseitig und lokal nach derselben Versionsregel beurteilt wird.
/// </summary>
public sealed class SyncService
{
    private readonly Database _database;
    private readonly ISyncTransport? _fixedTransport;
    private readonly SyncOptions _options;
    private readonly ChangeLogWriter _changes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISyncTransport? _transport;

    public SyncService(Database database, ISyncTransport? transport = null, SyncOptions? options = null)
    {
        _database = database;
        _fixedTransport = transport;
        _options = options ?? new SyncOptions();
        _changes = new ChangeLogWriter(database);
    }

    /// <summary>Wird ausgelöst, wenn im Reiter „Datenbank“ etwas gespeichert wurde.</summary>
    public event EventHandler? ConfigurationChanged;

    /// <summary>Aktives Ziel; ohne gespeicherte Zugangsdaten gibt es keines.</summary>
    public ISyncTransport? Transport
    {
        get
        {
            if (_fixedTransport is not null)
            {
                return _fixedTransport;
            }

            return _transport ??= CreateTransport();
        }
    }

    public bool IsConfigured => Transport is { IsConfigured: true };

    public string TransportName => Transport?.Name ?? "nicht eingerichtet";

    /// <summary>Offene Änderungen, die auf das Senden warten.</summary>
    public int PendingCount => _changes.GetPending().Count;

    /// <summary>Nach dem Speichern neuer Zugangsdaten aufrufen, damit sie gelten.</summary>
    public void Reload()
    {
        _transport = null;
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private ISyncTransport? CreateTransport()
    {
        var settings = new SyncSettingsStore(_database).Load();
        return settings is { IsComplete: true } ? new PostgresSyncTransport(_database, settings) : null;
    }

    public async Task<SyncResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transport = Transport;
            if (transport is not { IsConfigured: true })
            {
                var waiting = _changes.GetPending().Count;
                return new SyncResult(false, 0, 0, 0, true, waiting == 0
                    ? "Kein PostgreSQL-Ziel eingerichtet – die Daten bleiben lokal."
                    : $"Kein PostgreSQL-Ziel eingerichtet – {waiting} Änderung(en) warten lokal.");
            }

            for (var attempt = 1;; attempt++)
            {
                try
                {
                    return await RunOnceAsync(transport, attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (IsTransient(exception) && attempt < _options.MaxAttempts)
                {
                    // Verbindungsstörung: kurz warten und den Lauf wiederholen.
                    // Bereits bestätigte Pakete und Seiten bleiben gespeichert.
                    await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    return Fail(exception);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private SyncResult Fail(Exception exception)
    {
        var message = $"Synchronisierung nicht möglich: {exception.Message}";
        new SyncSettingsStore(_database).SetLastResult(DateTime.UtcNow, false, message);
        return new SyncResult(true, 0, 0, 0, false, message);
    }

    private async Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = _options.RetryDelay * Math.Pow(2, attempt - 1);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ein Versuch: erst senden, dann holen. Der Stand wird laufend gesichert.</summary>
    private async Task<SyncResult> RunOnceAsync(ISyncTransport transport, int attempt, CancellationToken cancellationToken)
    {
        var store = new SyncSettingsStore(_database);
        var pushed = 0;
        var pulled = 0;
        var skipped = 0;
        var errors = new List<string>();

        // 1. Offene lokale Änderungen paketweise senden.
        while (true)
        {
            var batch = _changes.GetPending(_options.PushChunkSize);
            if (batch.Count == 0)
            {
                break;
            }

            var snapshots = LocalSnapshotReader.Read(_database, batch);
            if (snapshots.Count > 0)
            {
                var outcome = await transport.PushAsync(snapshots, cancellationToken).ConfigureAwait(false);
                pushed += outcome.Accepted;

                if (outcome.Conflicts.Count > 0)
                {
                    // Der Server hatte denselben oder einen neueren Stand: er gilt.
                    var resolved = LocalChangeApplier.ApplyServerState(_database, outcome.Conflicts);
                    skipped += resolved.Skipped;
                    errors.AddRange(resolved.Errors);

                    if (resolved.Failed > 0)
                    {
                        // Nicht bestätigen – der nächste Lauf versucht es erneut.
                        break;
                    }
                }
            }

            // Auch Einträge ohne Abgleich (Dokumente und Bilder) werden bestätigt,
            // weil für sie bewusst nur die lokale Datei zählt.
            _changes.MarkSynced(batch.Select(entry => entry.Id).ToArray());

            if (batch.Count < _options.PushChunkSize)
            {
                break;
            }
        }

        // 2. Hereinkommende Änderungen seitenweise holen.
        for (var page = 0; page < _options.MaxPullPages; page++)
        {
            var cursor = store.LastChangeId;
            var incoming = await transport.PullAsync(cursor, cancellationToken).ConfigureAwait(false);
            if (incoming.Count == 0)
            {
                break;
            }

            var applied = LocalChangeApplier.Apply(_database, incoming);
            pulled += applied.Applied;
            skipped += applied.Skipped;
            errors.AddRange(applied.Errors);

            // Erst zählen, dann sichern: bricht der Lauf danach ab, beginnt der
            // nächste genau hier und überspringt nichts.
            var next = incoming.Max(change => change.ChangeId);
            if (next <= cursor)
            {
                // Der Server liefert keinen Fortschritt – lieber abbrechen als endlos lesen.
                break;
            }

            store.SetLastChangeId(next);
            if (incoming.Count < _options.PullPageSize)
            {
                break;
            }
        }

        var parts = new List<string>();
        if (pushed > 0)
        {
            parts.Add($"{pushed} gesendet");
        }

        if (pulled > 0)
        {
            parts.Add($"{pulled} übernommen");
        }

        if (skipped > 0)
        {
            parts.Add($"{skipped} unverändert");
        }

        if (errors.Count > 0)
        {
            parts.Add($"{errors.Count} fehlgeschlagen");
        }

        var message = parts.Count == 0
            ? "Alles ist bereits abgeglichen."
            : "Abgleich: " + string.Join(", ", parts) + ".";

        if (attempt > 1)
        {
            message += $" Vorherige Versuche scheiterten an einer Verbindungsstörung ({attempt - 1}).";
        }

        if (errors.Count > 0)
        {
            message += " " + errors[0];
        }

        var succeeded = errors.Count == 0;
        store.SetLastResult(DateTime.UtcNow, succeeded, message);
        return new SyncResult(true, pushed, pulled, errors.Count, succeeded, message);
    }

    /// <summary>Störungen, bei denen eine Wiederholung sinnvoll ist.</summary>
    private static bool IsTransient(Exception exception) => exception switch
    {
        NpgsqlException { IsTransient: true } => true,
        TimeoutException => true,
        IOException => true,
        SocketException => true,
        _ when exception.InnerException is not null => IsTransient(exception.InnerException),
        _ => false,
    };
}
