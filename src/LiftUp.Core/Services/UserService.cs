using System.Text.RegularExpressions;
using LiftUp.Core.Data;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;
using LiftUp.Core.Security;

namespace LiftUp.Core.Services;

/// <summary>
/// Benutzerverwaltung. Jede schreibende Aktion prüft das Recht
/// <see cref="Permission.ManageUsers"/> und schützt das letzte
/// aktive Administratorkonto vor dem Aussperren.
/// </summary>
public sealed partial class UserService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly UserRepository _users = new();
    private readonly RoleRepository _roles = new();
    private readonly ChangeLogWriter _changes;

    public UserService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database, _actor.UserName);
    }

    public IReadOnlyList<UserListItem> GetUsers(string? search = null)
    {
        _actor.Require(Permission.ManageUsers);
        using var connection = _database.Open();
        return _users.GetListItems(connection, null, search);
    }

    public UserListItem? GetUser(Guid id)
    {
        _actor.Require(Permission.ManageUsers);
        using var connection = _database.Open();
        return _users.GetListItems(connection, null, null).FirstOrDefault(item => item.Id == id);
    }

    public User Create(
        string userName,
        string displayName,
        Guid roleId,
        string password,
        bool mustChangePassword,
        string? company = null,
        string? email = null,
        string? phone = null,
        string? companyStreet = null,
        string? companyZip = null,
        string? companyCity = null,
        string? companyLogo = null)
    {
        _actor.Require(Permission.ManageUsers);

        userName = (userName ?? string.Empty).Trim();
        displayName = (displayName ?? string.Empty).Trim();
        ValidateUserName(userName);
        if (displayName.Length == 0)
        {
            displayName = userName;
        }

        PasswordPolicy.Validate(password);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        if (_roles.GetById(connection, transaction, roleId) is null)
        {
            throw new ValidationException("Die gewählte Rolle existiert nicht.");
        }

        if (_users.GetByUserName(connection, transaction, userName) is not null)
        {
            throw new ValidationException($"Der Benutzername \"{userName}\" ist bereits vergeben.");
        }

        var hash = PasswordHasher.Hash(password);
        var newId = Guid.NewGuid();
        var logo = PrepareCompanyLogo(companyLogo, newId, null);
        var user = new User
        {
            Id = newId,
            UserName = userName,
            DisplayName = displayName,
            Company = Normalize(company),
            CompanyStreet = Normalize(companyStreet),
            CompanyZip = Normalize(companyZip),
            CompanyCity = Normalize(companyCity),
            CompanyLogo = logo,
            Email = Normalize(email),
            Phone = Normalize(phone),
            RoleId = roleId,
            IsActive = true,
            PasswordHash = hash.HashBase64,
            PasswordSalt = hash.SaltBase64,
            PasswordIterations = hash.Iterations,
            MustChangePassword = mustChangePassword,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = _actor.UserName,
        };

        _users.Insert(connection, transaction, user);
        _changes.Record(connection, transaction, "User", user.Id.ToString("D"), "Create",
            new { user.UserName, user.DisplayName, RoleId = roleId.ToString("D"), user.IsActive, user.MustChangePassword },
            user.RowVersion);
        transaction.Commit();

        CopyCompanyLogo(user.Id, companyLogo, user.CompanyLogo);
        return user;
    }

    public void Update(
        Guid id,
        string displayName,
        Guid roleId,
        bool isActive,
        string? company = null,
        string? email = null,
        string? phone = null,
        string? companyStreet = null,
        string? companyZip = null,
        string? companyCity = null,
        string? companyLogo = null)
    {
        _actor.Require(Permission.ManageUsers);

        displayName = (displayName ?? string.Empty).Trim();

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var existing = _users.GetById(connection, transaction, id)
                       ?? throw new ValidationException("Der Benutzer wurde nicht gefunden.");
        var newRole = _roles.GetById(connection, transaction, roleId)
                      ?? throw new ValidationException("Die gewählte Rolle existiert nicht.");
        var oldRole = _roles.GetById(connection, transaction, existing.RoleId);

        if (displayName.Length == 0)
        {
            displayName = existing.UserName;
        }

        if (id == _actor.UserId && !isActive)
        {
            throw new ValidationException("Das eigene Konto kann nicht deaktiviert werden.");
        }

        var losesAdminRights =
            oldRole is not null && oldRole.Has(Permission.ManageUsers) && !newRole.Has(Permission.ManageUsers);
        if (losesAdminRights || (existing.IsActive && !isActive))
        {
            EnsureAnotherAdministratorExists(connection, transaction, id);
        }

        // Ein bereits gespeichertes Logo bleibt gültig, auch wenn die Datei gerade fehlt.
        var logo = PrepareCompanyLogo(companyLogo, id, existing.CompanyLogo);
        var rowVersion = _users.Update(
            connection,
            transaction,
            id,
            displayName,
            roleId,
            isActive,
            Normalize(company),
            Normalize(email),
            Normalize(phone),
            Normalize(companyStreet),
            Normalize(companyZip),
            Normalize(companyCity),
            logo);
        _changes.Record(connection, transaction, "User", id.ToString("D"), "Update",
            new { existing.UserName, DisplayName = displayName, RoleId = roleId.ToString("D"), IsActive = isActive },
            rowVersion);
        transaction.Commit();

        CopyCompanyLogo(id, companyLogo, logo);
    }

    public void ResetPassword(Guid id, string newPassword, bool mustChangePassword)
    {
        _actor.Require(Permission.ManageUsers);
        PasswordPolicy.Validate(newPassword);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var existing = _users.GetById(connection, transaction, id)
                       ?? throw new ValidationException("Der Benutzer wurde nicht gefunden.");

        var hash = PasswordHasher.Hash(newPassword);
        var rowVersion = _users.UpdatePassword(connection, transaction, id, hash.HashBase64, hash.SaltBase64,
            hash.Iterations, mustChangePassword);
        _changes.Record(connection, transaction, "User", id.ToString("D"), "PasswordReset",
            new { existing.UserName, PasswordChanged = true, MustChangePassword = mustChangePassword }, rowVersion);
        transaction.Commit();
    }

    public void Delete(Guid id)
    {
        _actor.Require(Permission.ManageUsers);

        if (id == _actor.UserId)
        {
            throw new ValidationException("Das eigene Konto kann nicht gelöscht werden.");
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var existing = _users.GetById(connection, transaction, id)
                       ?? throw new ValidationException("Der Benutzer wurde nicht gefunden.");

        EnsureAnotherAdministratorExists(connection, transaction, id);

        var rowVersion = _users.SoftDelete(connection, transaction, id);
        _changes.Record(connection, transaction, "User", id.ToString("D"), "Delete",
            new { existing.UserName }, rowVersion);
        transaction.Commit();
    }

    /// <summary>Ordner der Firmenlogos; liegt neben der Datenbank und gehört zum Arbeitsplatz.</summary>
    private string LogoFolder =>
        Path.Combine(Path.GetDirectoryName(_database.FilePath) ?? Path.GetTempPath(), "logos");

    /// <summary>
    /// Prüft das gewählte Firmenlogo und bestimmt seinen Platz im Logoordner. Die Datei wird
    /// beim Speichern dorthin kopiert, damit sie unabhängig von der Originaldatei bleibt.
    /// </summary>
    private string? PrepareCompanyLogo(string? path, Guid userId, string? currentLogo)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ValidationException("Der Pfad zum Firmenlogo ist ungültig.");
        }

        if (!File.Exists(fullPath))
        {
            // Das bisherige Logo bleibt eingetragen, auch wenn die Datei gerade fehlt.
            if (currentLogo is not null
                && string.Equals(fullPath, currentLogo, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            throw new ValidationException("Die gewählte Logodatei wurde nicht gefunden.");
        }

        if (ImageEmbedding.ContentTypeFor(fullPath) is null)
        {
            throw new ValidationException(
                $"Der Dateityp \"{Path.GetExtension(fullPath)}\" wird für das Firmenlogo nicht unterstützt. " +
                "Möglich sind PNG, JPG, GIF, WebP, BMP, SVG und TIFF.");
        }

        var folder = LogoFolder;
        if (!folder.EndsWith(Path.DirectorySeparatorChar))
        {
            folder += Path.DirectorySeparatorChar;
        }

        if (fullPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return Path.Combine(LogoFolder, userId.ToString("D") + Path.GetExtension(fullPath).ToLowerInvariant());
    }

    /// <summary>
    /// Kopiert das Logo nach dem Speichern in den Logoordner. Eine ältere Datei mit anderer
    /// Endung wird dabei entfernt, damit kein Rest liegen bleibt.
    /// </summary>
    private void CopyCompanyLogo(Guid userId, string? sourcePath, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        string source;
        try
        {
            source = Path.GetFullPath(sourcePath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (string.Equals(source, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(LogoFolder);
            File.Copy(source, targetPath, overwrite: true);

            foreach (var file in Directory.GetFiles(LogoFolder, userId.ToString("D") + ".*"))
            {
                if (!string.Equals(file, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LiftUpException(
                "Der Benutzer wurde gespeichert, das Firmenlogo konnte aber nicht kopiert werden: "
                + exception.Message);
        }
    }

    private void EnsureAnotherAdministratorExists(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        Guid excludingUserId)
    {
        var remaining = _users.CountActiveUsersWithPermission(connection, transaction, Permission.ManageUsers, excludingUserId);
        if (remaining == 0)
        {
            throw new ValidationException(
                "Das ist der letzte aktive Administrator. Bitte zuerst einem anderen Benutzer die Benutzerverwaltung geben.");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateUserName(string userName)
    {
        if (userName.Length < 3)
        {
            throw new ValidationException("Der Benutzername muss mindestens 3 Zeichen lang sein.");
        }

        if (!UserNamePattern().IsMatch(userName))
        {
            throw new ValidationException("Der Benutzername darf nur Buchstaben, Ziffern, Punkt, Bindestrich und Unterstrich enthalten.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNamePattern();
}
