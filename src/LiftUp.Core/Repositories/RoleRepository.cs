using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

public sealed class RoleRepository
{
    private const string Columns = "Id, Name, Description, Permissions, IsSystem, CreatedUtc, RowVersion";

    public IReadOnlyList<Role> GetAll(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = Db.Command(connection,
            $"SELECT {Columns} FROM Roles ORDER BY IsSystem DESC, Name COLLATE NOCASE;", transaction);
        using var reader = command.ExecuteReader();
        var roles = new List<Role>();
        while (reader.Read())
        {
            roles.Add(Map(reader));
        }

        return roles;
    }

    public Role? GetById(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, $"SELECT {Columns} FROM Roles WHERE Id = $id;", transaction);
        command.With("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public Role? GetByName(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var command = Db.Command(connection, $"SELECT {Columns} FROM Roles WHERE Name = $name;", transaction);
        command.With("$name", name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Insert(SqliteConnection connection, SqliteTransaction? transaction, Role role)
    {
        using var command = Db.Command(connection, """
            INSERT INTO Roles (Id, Name, Description, Permissions, IsSystem, CreatedUtc, RowVersion)
            VALUES ($id, $name, $description, $permissions, $isSystem, $createdUtc, 1);
            """, transaction);
        command.With("$id", role.Id.ToString("D"))
            .With("$name", role.Name)
            .With("$description", role.Description)
            .With("$permissions", (int)role.Permissions)
            .With("$isSystem", role.IsSystem ? 1 : 0)
            .With("$createdUtc", role.CreatedUtc.ToDb());
        command.ExecuteNonQuery();
        role.RowVersion = 1;
    }

    public void Update(SqliteConnection connection, SqliteTransaction? transaction, Role role)
    {
        using var command = Db.Command(connection, """
            UPDATE Roles
            SET Name = $name, Description = $description, Permissions = $permissions, RowVersion = RowVersion + 1
            WHERE Id = $id;
            """, transaction);
        command.With("$id", role.Id.ToString("D"))
            .With("$name", role.Name)
            .With("$description", role.Description)
            .With("$permissions", (int)role.Permissions);
        command.ExecuteNonQuery();
        role.RowVersion++;
    }

    public void Delete(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, "DELETE FROM Roles WHERE Id = $id;", transaction);
        command.With("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    public int CountUsers(SqliteConnection connection, SqliteTransaction? transaction, Guid roleId)
    {
        using var command = Db.Command(connection,
            "SELECT COUNT(1) FROM Users WHERE RoleId = $roleId AND IsDeleted = 0;", transaction);
        command.With("$roleId", roleId.ToString("D"));
        return (int)Convert.ToInt64(command.ExecuteScalar());
    }

    private static Role Map(SqliteDataReader reader) => new()
    {
        Id = reader.Guid(0),
        Name = reader.Text(1),
        Description = reader.Text(2),
        Permissions = (Permission)reader.Int32(3),
        IsSystem = reader.Bool(4),
        CreatedUtc = reader.Date(5),
        RowVersion = reader.Int64(6),
    };
}
