using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;
using LiftUp.Core.Security;

namespace LiftUp.Core.Services;

public sealed record LoginResult(bool Success, Session? Session, string? Error)
{
    public static LoginResult Ok(Session session) => new(true, session, null);
    public static LoginResult Fail(string error) => new(false, null, error);
}

/// <summary>Anmeldung, Passwortwechsel und Auffrischen der Sitzung.</summary>
public sealed class AuthService
{
    private readonly Database _database;
    private readonly UserRepository _users = new();
    private readonly RoleRepository _roles = new();
    private readonly ChangeLogWriter _changes;

    public AuthService(Database database)
    {
        _database = database;
        _changes = new ChangeLogWriter(database);
    }

    public LoginResult Login(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            return LoginResult.Fail("Bitte Benutzername und Passwort eingeben.");
        }

        using var connection = _database.Open();
        var user = _users.GetByUserName(connection, null, userName.Trim());
        if (user is null)
        {
            return LoginResult.Fail("Benutzername oder Passwort ist falsch.");
        }

        if (!user.IsActive)
        {
            return LoginResult.Fail("Dieses Benutzerkonto ist deaktiviert. Bitte an einen Administrator wenden.");
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            return LoginResult.Fail("Benutzername oder Passwort ist falsch.");
        }

        var role = _roles.GetById(connection, null, user.RoleId);
        if (role is null)
        {
            return LoginResult.Fail("Die Rolle des Benutzers existiert nicht mehr. Bitte an einen Administrator wenden.");
        }

        _users.UpdateLastLogin(connection, null, user.Id);
        return LoginResult.Ok(Session.Create(user, role));
    }

    /// <summary>Ändert das Passwort des angemeldeten Benutzers (auch die erzwungene Erstanpassung).</summary>
    public Session ChangePassword(Session session, string currentPassword, string newPassword)
    {
        using var connection = _database.Open();
        var user = _users.GetById(connection, null, session.UserId)
                   ?? throw new ValidationException("Das Benutzerkonto wurde nicht gefunden.");

        if (!PasswordHasher.Verify(currentPassword ?? string.Empty, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            throw new ValidationException("Das aktuelle Passwort ist falsch.");
        }

        PasswordPolicy.Validate(newPassword);
        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            throw new ValidationException("Das neue Passwort muss sich vom aktuellen unterscheiden.");
        }

        var hash = PasswordHasher.Hash(newPassword);
        using var transaction = connection.BeginTransaction();
        var rowVersion = _users.UpdatePassword(connection, transaction, user.Id, hash.HashBase64, hash.SaltBase64, hash.Iterations, mustChange: false);
        _changes.Record(connection, transaction, "User", user.Id.ToString("D"), "PasswordChanged",
            new { user.UserName, PasswordChanged = true }, rowVersion);
        transaction.Commit();

        // Sitzung mit aktualisierten Werten zurückgeben (MustChangePassword ist jetzt false).
        var role = _roles.GetById(connection, null, user.RoleId)
                   ?? throw new ValidationException("Die Rolle des Benutzers existiert nicht mehr.");
        user.MustChangePassword = false;
        return Session.Create(user, role);
    }

    /// <summary>Liest Rechte und Rollenname neu ein, z. B. nach einer Rollenänderung.</summary>
    public Session Refresh(Session session)
    {
        using var connection = _database.Open();
        var user = _users.GetById(connection, null, session.UserId)
                   ?? throw new ValidationException("Das Benutzerkonto wurde nicht gefunden.");
        var role = _roles.GetById(connection, null, user.RoleId)
                   ?? throw new ValidationException("Die Rolle des Benutzers existiert nicht mehr.");
        return Session.Create(user, role);
    }
}
