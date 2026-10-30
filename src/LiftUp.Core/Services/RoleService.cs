using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;

namespace LiftUp.Core.Services;

/// <summary>Rollen- und Rechteverwaltung.</summary>
public sealed class RoleService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly RoleRepository _roles = new();
    private readonly ChangeLogWriter _changes;

    public RoleService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database);
    }

    /// <summary>Rollenliste – für Zuweisungen auch ohne Verwaltungsrecht lesbar.</summary>
    public IReadOnlyList<Role> GetAll()
    {
        using var connection = _database.Open();
        return _roles.GetAll(connection);
    }

    public Role? GetById(Guid id)
    {
        using var connection = _database.Open();
        return _roles.GetById(connection, null, id);
    }

    public Role Create(string name, string description, Permission permissions)
    {
        _actor.Require(Permission.ManageRoles);
        name = (name ?? string.Empty).Trim();
        if (name.Length < 2)
        {
            throw new ValidationException("Der Rollenname muss mindestens 2 Zeichen lang sein.");
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        if (_roles.GetByName(connection, transaction, name) is not null)
        {
            throw new ValidationException($"Eine Rolle mit dem Namen \"{name}\" existiert bereits.");
        }

        var role = new Role
        {
            Name = name,
            Description = (description ?? string.Empty).Trim(),
            Permissions = permissions & Permission.All,
            IsSystem = false,
            CreatedUtc = DateTime.UtcNow,
        };

        _roles.Insert(connection, transaction, role);
        _changes.Record(connection, transaction, "Role", role.Id.ToString("D"), "Create",
            new { role.Name, role.Description, Permissions = (int)role.Permissions }, role.RowVersion);
        transaction.Commit();
        return role;
    }

    public Role Update(Guid id, string name, string description, Permission permissions)
    {
        _actor.Require(Permission.ManageRoles);
        name = (name ?? string.Empty).Trim();

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var role = _roles.GetById(connection, transaction, id)
                   ?? throw new ValidationException("Die Rolle wurde nicht gefunden.");

        if (role.IsSystem && !string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Systemrollen können nicht umbenannt werden.");
        }

        if (name.Length < 2)
        {
            throw new ValidationException("Der Rollenname muss mindestens 2 Zeichen lang sein.");
        }

        var duplicate = _roles.GetByName(connection, transaction, name);
        if (duplicate is not null && duplicate.Id != id)
        {
            throw new ValidationException($"Eine Rolle mit dem Namen \"{name}\" existiert bereits.");
        }

        var effectivePermissions = permissions & Permission.All;

        // Der Administrator bleibt immer voll berechtigt und kann sich so nicht selbst aussperren.
        if (role.IsSystem && string.Equals(role.Name, "Administrator", StringComparison.OrdinalIgnoreCase))
        {
            effectivePermissions = Permission.All;
        }

        if (role.Id == _actor.RoleId && !effectivePermissions.HasFlag(Permission.ManageRoles))
        {
            throw new ValidationException("Der eigenen Rolle kann das Recht zur Rechteverwaltung nicht entzogen werden.");
        }

        role.Name = name;
        role.Description = (description ?? string.Empty).Trim();
        role.Permissions = effectivePermissions;

        _roles.Update(connection, transaction, role);
        _changes.Record(connection, transaction, "Role", role.Id.ToString("D"), "Update",
            new { role.Name, role.Description, Permissions = (int)role.Permissions }, role.RowVersion);
        transaction.Commit();
        return role;
    }

    public void Delete(Guid id)
    {
        _actor.Require(Permission.ManageRoles);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var role = _roles.GetById(connection, transaction, id)
                   ?? throw new ValidationException("Die Rolle wurde nicht gefunden.");

        if (role.IsSystem)
        {
            throw new ValidationException("Systemrollen können nicht gelöscht werden.");
        }

        if (role.Id == _actor.RoleId)
        {
            throw new ValidationException("Die eigene Rolle kann nicht gelöscht werden.");
        }

        var assignedUsers = _roles.CountUsers(connection, transaction, id);
        if (assignedUsers > 0)
        {
            throw new ValidationException(
                $"Die Rolle ist noch {assignedUsers} Benutzer(n) zugewiesen. Bitte zuerst umstellen.");
        }

        _roles.Delete(connection, transaction, id);
        _changes.Record(connection, transaction, "Role", id.ToString("D"), "Delete", new { role.Name }, role.RowVersion);
        transaction.Commit();
    }
}
