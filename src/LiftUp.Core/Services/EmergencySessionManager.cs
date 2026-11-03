using LiftUp.Core.Domain;

namespace LiftUp.Core.Services;

/// <summary>
/// Überwacht die Notfall-Sitzung: Jede Bedienung verlängert sie, nach Ablauf der
/// Inaktivitätszeit gilt sie als beendet. Die Uhr ist austauschbar, damit sich der Ablauf
/// ohne Warten prüfen lässt.
/// </summary>
public sealed class EmergencySessionManager
{
    private readonly Func<DateTime> _clock;
    private DateTime _expiresUtc;

    public EmergencySessionManager(Session session, TimeSpan timeout, Func<DateTime>? clock = null)
    {
        if (!session.IsEmergency)
        {
            throw new ArgumentException("Nur eine Notfall-Sitzung lässt sich überwachen.", nameof(session));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Session = session;
        Timeout = timeout;
        _clock = clock ?? (() => DateTime.UtcNow);
        _expiresUtc = _clock() + timeout;
    }

    public Session Session { get; }

    public TimeSpan Timeout { get; }

    /// <summary>Zeitpunkt, zu dem die Sitzung ohne weitere Bedienung endet.</summary>
    public DateTime ExpiresUtc => _expiresUtc;

    public TimeSpan Remaining => IsExpired ? TimeSpan.Zero : _expiresUtc - _clock();

    public bool IsExpired => _clock() >= _expiresUtc;

    /// <summary>Verlängert die Sitzung nach einer Bedienung.</summary>
    public void Touch()
    {
        if (!IsExpired)
        {
            _expiresUtc = _clock() + Timeout;
        }
    }
}
