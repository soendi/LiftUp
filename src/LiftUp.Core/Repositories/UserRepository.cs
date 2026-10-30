using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

public sealed class UserRepository
{
    private const string Columns =
        "Id, UserName, DisplayName, RoleId, IsActive, PasswordHash, PasswordSalt, PasswordIterations, " +
        "MustChangePassword, CreatedUtc, CreatedBy, LastLoginUtc, RowVersion, IsDeleted";

    public IReadOnlyList<UserListItem> GetListItems(SqliteConnection connection, SqliteTransaction? transaction, string? search)
    {
        var filter = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : " AND (u.UserName LIKE $pattern OR u.DisplayName LIKE $pattern)";

        using var command = Db.Command(connection, $"""
            SELECT u.Id, u.UserName, u.DisplayName, u.IsActive, u.LastLoginUtc, r.Name, r.Permissions
            FROM Users u
            JOIN Roles r ON r.Id = u.RoleId
            WHERE u.IsDeleted = 0{filter}
            ORDER BY u.UserName COLLATE NOCASE;
            """, transaction);
        if (!string.IsNullOrWhiteSpace(search))
        {
            command.With("$pattern", $"%{search.Trim()}%");
        }

        using var reader = command.ExecuteReader();
        var items = new List<UserListItem>();
        while (reader.Read())
        {
            var lastLogin = reader.DateOrNull(4);
            items.Add(new UserListItem
            {
                Id = reader.Guid(0),
                UserName = reader.Text(1),
                DisplayName = reader.Text(2),
                IsActive = reader.Bool(3),
                LastLoginText = lastLogin.HasValue
                    ? lastLogin.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                    : "–",
                RoleName = reader.Text(5),
                PermissionSummary = PermissionCatalog.Describe((Permission)reader.Int32(6)),
            });
        }

        return items;
    }

    public User? GetById(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, $"SELECT {Columns} FROM Users WHERE Id = $id;", transaction);
        command.With("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public User? GetByUserName(SqliteConnection connection, SqliteTransaction? transaction, string userName)
    {
        using var command = Db.Command(connection,
            $"SELECT {Columns} FROM Users WHERE UserName = $userName AND IsDeleted = 0;", transaction);
        command.With("$userName", userName);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Insert(SqliteConnection connection, SqliteTransaction? transaction, User user)
    {
        using var command = Db.Command(connection, """
            INSERT INTO Users
                (Id, UserName, DisplayName, RoleId, IsActive, PasswordHash, PasswordSalt, PasswordIterations,
                 MustChangePassword, CreatedUtc, CreatedBy, LastLoginUtc, RowVersion, IsDeleted)
            VALUES
                ($id, $userName, $displayName, $roleId, $isActive, $hash, $salt, $iterations,
                 $mustChange, $createdUtc, $createdBy, NULL, 1, 0);
            """, transaction);
        command.With("$id", user.Id.ToString("D"))
            .With("$userName", user.UserName)
            .With("$displayName", user.DisplayName)
            .With("$roleId", user.RoleId.ToString("D"))
            .With("$isActive", user.IsActive ? 1 : 0)
            .With("$hash", user.PasswordHash)
            .With("$salt", user.PasswordSalt)
            .With("$iterations", user.PasswordIterations)
            .With("$mustChange", user.MustChangePassword ? 1 : 0)
            .With("$createdUtc", user.CreatedUtc.ToDb())
            .With("$createdBy", user.CreatedBy);
        command.ExecuteNonQuery();
        user.RowVersion = 1;
    }

    public long Update(SqliteConnection connection, SqliteTransaction? transaction, Guid id, string displayName, Guid roleId, bool isActive)
    {
        using var command = Db.Command(connection, """
            UPDATE Users
            SET DisplayName = $displayName, RoleId = $roleId, IsActive = $isActive, RowVersion = RowVersion + 1
            WHERE Id = $id
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", id.ToString("D"))
            .With("$displayName", displayName)
            .With("$roleId", roleId.ToString("D"))
            .With("$isActive", isActive ? 1 : 0);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public long UpdatePassword(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid id,
        string hash,
        string salt,
        int iterations,
        bool mustChange)
    {
        using var command = Db.Command(connection, """
            UPDATE Users
            SET PasswordHash = $hash, PasswordSalt = $salt, PasswordIterations = $iterations,
                MustChangePassword = $mustChange, RowVersion = RowVersion + 1
            WHERE Id = $id
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", id.ToString("D"))
            .With("$hash", hash)
            .With("$salt", salt)
            .With("$iterations", iterations)
            .With("$mustChange", mustChange ? 1 : 0);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void UpdateLastLogin(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection,
            "UPDATE Users SET LastLoginUtc = $lastLogin WHERE Id = $id;", transaction);
        command.With("$id", id.ToString("D")).With("$lastLogin", DateTime.UtcNow.ToDb());
        command.ExecuteNonQuery();
    }

    public long SoftDelete(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, """
            UPDATE Users
            SET IsDeleted = 1, IsActive = 0, RowVersion = RowVersion + 1
            WHERE Id = $id
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", id.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Anzahl aktiver Benutzer, denen über ihre Rolle ein bestimmtes Recht zusteht.</summary>
    public int CountActiveUsersWithPermission(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Permission permission,
        Guid? excludingUserId = null)
    {
        using var command = Db.Command(connection, """
            SELECT COUNT(1)
            FROM Users u
            JOIN Roles r ON r.Id = u.RoleId
            WHERE u.IsDeleted = 0 AND u.IsActive = 1
              AND (r.Permissions & $mask) = $mask
              AND ($exclude IS NULL OR u.Id <> $exclude);
            """, transaction);
        command.With("$mask", (int)permission).With("$exclude", excludingUserId?.ToString("D"));
        return (int)Convert.ToInt64(command.ExecuteScalar());
    }

    private static User Map(SqliteDataReader reader) => new()
    {
        Id = reader.Guid(0),
        UserName = reader.Text(1),
        DisplayName = reader.Text(2),
        RoleId = reader.Guid(3),
        IsActive = reader.Bool(4),
        PasswordHash = reader.Text(5),
        PasswordSalt = reader.Text(6),
        PasswordIterations = reader.Int32(7),
        MustChangePassword = reader.Bool(8),
        CreatedUtc = reader.Date(9),
        CreatedBy = reader.TextOrNull(10),
        LastLoginUtc = reader.DateOrNull(11),
        RowVersion = reader.Int64(12),
        IsDeleted = reader.Bool(13),
    };
}
