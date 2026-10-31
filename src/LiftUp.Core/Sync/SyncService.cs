using System.Globalization;
using LiftUp.Core.Data;

namespace LiftUp.Core.Sync;

/// <summary>Eine Änderung, die von einem anderen Arbeitsplatz hereinkommt.</summary>
public sealed record RemoteChange(
    long ServerId,
    string EntityType,
    string EntityId,
    string Operation,
    string Payload,
    long RowVersion,
    DateTime ChangedUtc,
    string OriginDeviceId,
    string OriginUser = "");

/// <summary>Fehler beim Abgleich, bereits in einer für die Oberfläche tauglichen Form.</summary>
public sealed class SyncException(string message) : Exception(message);

/// <summary>
/// Transport zur zentralen PostgreSQL-Datenbank. Der Abgleich überträgt das
/// Änderungsprotokoll (Outbox): eigene offene Einträge hinauf, Einträge der
/// anderen Arbeitsplätze wieder herunter. Feldinhalte selbst werden nicht
/// übertragen – die Nutzlast nennt nur, was sich geändert hat.
/// </summary>
public interface ISyncTransport
{
    string Name { get; }

    /// <summary>Kennung des Ziels, damit der Lesestand je Server getrennt bleibt.</summary>
    string ScopeKey { get; }

    bool IsConfigured { get; }

    Task PushAsync(IReadOnlyList<ChangeLogEntry> changes, CancellationToken cancellationToken);

    /// <summary>Einträge anderer Arbeitsplätze ab der Servernummer <paramref name="sinceServerId"/>.</summary>
    Task<IReadOnlyList<RemoteChange>> PullAsync(long sinceServerId, CancellationToken cancellationToken);
}

/// <summary>Standard, solange kein Server eingerichtet ist: alles bleibt lokal.</summary>
public sealed class NotConfiguredSyncTransport : ISyncTransport
{
    public string Name => "nicht eingerichtet";
    public string ScopeKey => "lokal";
    public bool IsConfigured => false;

    public Task PushAsync(IReadOnlyList<ChangeLogEntry> changes, CancellationToken cancellationToken) =>
        throw new SyncException("Es ist kein Synchronisationsziel eingerichtet.");

    public Task<IReadOnlyList<RemoteChange>> PullAsync(long sinceServerId, CancellationToken cancellationToken) =>
        throw new SyncException("Es ist kein Synchronisationsziel eingerichtet.");
}

/// <summary>
/// Ergebnis eines Abgleichs: übertragene und übernommene Einträge sowie die
/// Zahl der weiterhin offenen. <see cref="Pending"/> ist 0, wenn alles beim
/// Server angekommen ist.
/// </summary>
public sealed record SyncResult(bool Attempted, int Pushed, int Pulled, int Pending, string Message);

/// <summary>
/// Führt den Abgleich aus: eigene offene Einträge aus dem Änderungsprotokoll
/// hinauf, Einträge der anderen Arbeitsplätze herunter. Erst nach erfolgreicher
/// Übertragung werden die Einträge als abgeglichen vermerkt; der Lesestand des
/// Servers wird zusammen mit den übernommenen Einträgen in einer Transaktion
/// gespeichert, damit nichts doppelt oder gar nicht ankommt.
/// </summary>
public sealed class SyncService(Database database, ISyncTransport transport)
{
    private const int PageSize = 200;

    /// <summary>Obergrenze je Lauf, damit ein großer Rückstand nicht endlos läuft.</summary>
    private const int MaximumPages = 100;

    private readonly ChangeLogWriter _changes = new(database);

    public bool IsConfigured => transport.IsConfigured;
    public string TransportName => transport.Name;
    public string ScopeKey => transport.ScopeKey;

    /// <summary>Offene Einträge, die beim nächsten Abgleich hinaufgehen.</summary>
    public int PendingCount => _changes.GetPending().Count;

    /// <summary>Zeitpunkt des letzten erfolgreichen Abgleichs, in Ortszeit.</summary>
    public DateTime? GetLastRunUtc()
    {
        var value = database.GetSetting(LastRunKey);
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    /// <summary>Serversequenz, bis zu der bereits übernommen wurde.</summary>
    public long GetWatermark()
    {
        var value = database.GetSetting(WatermarkKey);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    public async Task<SyncResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!transport.IsConfigured)
        {
            return new SyncResult(false, 0, 0, _changes.GetPending().Count,
                "Es ist kein PostgreSQL-Ziel eingerichtet. Die Daten bleiben lokal und werden später übertragen.");
        }

        var pushed = 0;
        var pulled = 0;

        try
        {
            for (var page = 0; page < MaximumPages; page++)
            {
                var pending = _changes.GetPending(PageSize);
                if (pending.Count == 0)
                {
                    break;
                }

                await transport.PushAsync(pending, cancellationToken).ConfigureAwait(false);
                _changes.MarkSynced(pending.Select(entry => entry.Id).ToArray());
                pushed += pending.Count;

                if (pending.Count < PageSize)
                {
                    break;
                }
            }

            var watermark = GetWatermark();
            for (var page = 0; page < MaximumPages; page++)
            {
                var remote = await transport.PullAsync(watermark, cancellationToken).ConfigureAwait(false);
                if (remote.Count == 0)
                {
                    break;
                }

                watermark = remote[^1].ServerId;
                ApplyRemote(remote, watermark);
                pulled += remote.Count;

                if (remote.Count < PageSize)
                {
                    break;
                }
            }
        }
        catch (SyncException exception)
        {
            return new SyncResult(true, pushed, pulled, _changes.GetPending().Count,
                $"Der Abgleich wurde unterbrochen: {exception.Message}");
        }

        database.SetSetting(LastRunKey, DateTime.UtcNow.ToDb());

        var remaining = _changes.GetPending().Count;
        var message = (pushed, pulled) switch
        {
            (0, 0) => "Alles ist bereits übertragen.",
            (0, _) => $"{pulled} Änderung(en) vom Server übernommen.",
            (_, 0) => $"{pushed} Änderung(en) übertragen.",
            _ => $"{pushed} Änderung(en) übertragen, {pulled} vom Server übernommen.",
        };

        return new SyncResult(true, pushed, pulled, remaining, message);
    }

    private string WatermarkKey => "SyncWatermark|" + transport.ScopeKey;

    private string LastRunKey => "SyncLastRun|" + transport.ScopeKey;

    /// <summary>
    /// Schreibt die Einträge anderer Arbeitsplätze ins lokale Protokoll und merkt
    /// sich den neuen Lesestand – beides in einer Transaktion. Die Einträge gelten
    /// sofort als abgeglichen, damit sie nicht wieder hinaufgeschickt werden.
    /// </summary>
    private void ApplyRemote(IReadOnlyList<RemoteChange> changes, long newWatermark)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var change in changes)
        {
            using var insert = Db.Command(connection, """
                INSERT INTO ChangeLog
                    (EntityType, EntityId, Operation, Payload, ChangedUtc, OriginDeviceId, RowVersion, OriginUser, SyncedUtc)
                VALUES
                    ($entityType, $entityId, $operation, $payload, $changedUtc, $origin, $rowVersion, $originUser, $syncedUtc);
                """, transaction);

            insert.With("$entityType", change.EntityType)
                .With("$entityId", change.EntityId)
                .With("$operation", change.Operation)
                .With("$payload", change.Payload)
                .With("$changedUtc", change.ChangedUtc.ToDb())
                .With("$origin", change.OriginDeviceId)
                .With("$rowVersion", change.RowVersion)
                .With("$originUser", string.IsNullOrWhiteSpace(change.OriginUser) ? DBNull.Value : change.OriginUser)
                .With("$syncedUtc", DateTime.UtcNow.ToDb());
            insert.ExecuteNonQuery();
        }

        using var setting = Db.Command(connection, """
            INSERT INTO Settings (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """, transaction);
        setting.With("$key", WatermarkKey)
            .With("$value", newWatermark.ToString(CultureInfo.InvariantCulture));
        setting.ExecuteNonQuery();

        transaction.Commit();
    }
}
