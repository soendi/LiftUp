using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;
using LiftUp.Core.Security;

namespace LiftUp.Core.Services;

/// <summary>Ergebnis einer Notfallanmeldung mit MasterKey-Code.</summary>
public sealed record EmergencyLoginResult(bool Success, Session? Session, string? Error)
{
    public static EmergencyLoginResult Ok(Session session) => new(true, session, null);
    public static EmergencyLoginResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Notfallanmeldung über einen signierten MasterKey-Lizenzcode. Die Prüfung läuft vollständig
/// offline. Erst nach erfolgreicher Signatur- und Gültigkeitsprüfung entsteht eine Sitzung mit
/// allen Rechten; sie dient ausschliesslich der Wiederherstellung regulärer Benutzerzugänge.
/// Der Code selbst wird nirgends gespeichert oder protokolliert.
/// </summary>
public sealed class EmergencyAuthenticationService
{
    /// <summary>Einstellung für die Inaktivitätszeit der Notfall-Sitzung in Minuten.</summary>
    public const string TimeoutSettingKey = "EmergencySessionMinutes";

    public const int DefaultTimeoutMinutes = 15;
    public const int MinimumTimeoutMinutes = 5;
    public const int MaximumTimeoutMinutes = 120;

    private readonly Database _database;
    private readonly UserRepository _users = new();

    public EmergencyAuthenticationService(Database database)
    {
        _database = database;
    }

    /// <summary>Inaktivitätszeit der Notfall-Sitzung aus den Einstellungen.</summary>
    public int TimeoutMinutes => ReadTimeoutMinutes(_database);

    public static int ReadTimeoutMinutes(Database database)
    {
        var raw = database.GetSetting(TimeoutSettingKey);
        return int.TryParse(raw, out var minutes)
            ? Math.Clamp(minutes, MinimumTimeoutMinutes, MaximumTimeoutMinutes)
            : DefaultTimeoutMinutes;
    }

    /// <summary>Stellt die Inaktivitätszeit ein; Werte ausserhalb des Bereichs werden begrenzt.</summary>
    public int SetTimeoutMinutes(int minutes)
    {
        var clamped = Math.Clamp(minutes, MinimumTimeoutMinutes, MaximumTimeoutMinutes);
        _database.SetSetting(TimeoutSettingKey, clamped.ToString());
        return clamped;
    }

    /// <summary>
    /// Prüft den Code und erstellt bei Erfolg eine Notfall-Sitzung mit allen Rechten.
    /// Benutzername und Passwort sind dafür nicht nötig.
    /// </summary>
    public EmergencyLoginResult Login(string? code, DateTime? nowUtc = null, byte[]? publicKey = null)
    {
        var validation = MasterKeyCodeValidator.Validate(code, nowUtc, publicKey);
        if (!validation.IsValid)
        {
            return EmergencyLoginResult.Fail(validation.Message);
        }

        using var connection = _database.Open();
        var account = _users.GetEmergencyAccount(connection, null);
        if (account is null)
        {
            return EmergencyLoginResult.Fail(
                "Es ist kein aktives Benutzerkonto vorhanden, auf das sich der Notfallzugang stützen kann.");
        }

        var session = Session.CreateEmergency(account, DateTime.UtcNow.AddMinutes(TimeoutMinutes));
        Log(session, "Login", new
        {
            Method = "MasterKey",
            ValidFromUtc = validation.IssuedUtc,
            ValidUntilUtc = validation.ExpiresUtc,
        });

        return EmergencyLoginResult.Ok(session);
    }

    /// <summary>Protokolliert das Ende der Notfall-Sitzung.</summary>
    public void Logout(Session session)
    {
        if (!session.IsEmergency)
        {
            return;
        }

        Log(session, "Logout", new { Reason = "Beendet" });
    }

    /// <summary>
    /// Schreibt den Vorgang ins Änderungsprotokoll. Bewusst ohne Code, ohne Passwörter und
    /// ohne Schlüsselwerte.
    /// </summary>
    private void Log(Session session, string operation, object payload)
    {
        using var connection = _database.Open();
        new ChangeLogWriter(_database, session.ActorName).Record(
            connection,
            null,
            "EmergencyAccess",
            session.UserId.ToString("D"),
            operation,
            payload,
            0);
    }
}
