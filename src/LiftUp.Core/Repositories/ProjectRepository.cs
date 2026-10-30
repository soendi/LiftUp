using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

public sealed class ProjectRepository
{
    private const string Columns =
        "Id, Number, Name, Customer, Description, FolderPath, Status, CreatedUtc, CreatedBy, UpdatedUtc, UpdatedBy, RowVersion, IsDeleted";

    public IReadOnlyList<ProjectListItem> Search(SqliteConnection connection, SqliteTransaction? transaction, string? search)
    {
        var filter = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : " AND (p.Name LIKE $pattern OR p.Number LIKE $pattern OR p.Customer LIKE $pattern OR p.FolderPath LIKE $pattern)";

        using var command = Db.Command(connection, $"""
            SELECT p.Id, p.Number, p.Name, p.Customer, p.FolderPath, p.Status, p.UpdatedUtc, p.UpdatedBy
            FROM Projects p
            WHERE p.IsDeleted = 0{filter}
            ORDER BY p.Name COLLATE NOCASE;
            """, transaction);
        if (!string.IsNullOrWhiteSpace(search))
        {
            command.With("$pattern", $"%{search.Trim()}%");
        }

        using var reader = command.ExecuteReader();
        var items = new List<ProjectListItem>();
        while (reader.Read())
        {
            var folder = reader.Text(4);
            items.Add(new ProjectListItem
            {
                Id = reader.Guid(0),
                Number = reader.Text(1),
                Name = reader.Text(2),
                Customer = reader.Text(3),
                FolderPath = folder,
                Status = (ProjectStatus)reader.Int32(5),
                UpdatedText = reader.Date(6).ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                UpdatedBy = reader.Text(7),
                FolderState = Directory.Exists(folder) ? "vorhanden" : "fehlt",
            });
        }

        return items;
    }

    public Project? GetById(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection,
            $"SELECT {Columns} FROM Projects WHERE Id = $id AND IsDeleted = 0;", transaction);
        command.With("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public bool NumberExists(SqliteConnection connection, SqliteTransaction? transaction, string number, Guid? excludingProjectId)
    {
        using var command = Db.Command(connection, """
            SELECT COUNT(1) FROM Projects
            WHERE Number = $number AND IsDeleted = 0 AND ($exclude IS NULL OR Id <> $exclude);
            """, transaction);
        command.With("$number", number).With("$exclude", excludingProjectId?.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public bool FolderExists(SqliteConnection connection, SqliteTransaction? transaction, string folderPath, Guid? excludingProjectId)
    {
        using var command = Db.Command(connection, """
            SELECT COUNT(1) FROM Projects
            WHERE FolderPath = $folderPath AND IsDeleted = 0 AND ($exclude IS NULL OR Id <> $exclude);
            """, transaction);
        command.With("$folderPath", folderPath).With("$exclude", excludingProjectId?.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public void Insert(SqliteConnection connection, SqliteTransaction? transaction, Project project)
    {
        using var command = Db.Command(connection, """
            INSERT INTO Projects
                (Id, Number, Name, Customer, Description, FolderPath, Status,
                 CreatedUtc, CreatedBy, UpdatedUtc, UpdatedBy, RowVersion, IsDeleted)
            VALUES
                ($id, $number, $name, $customer, $description, $folderPath, $status,
                 $createdUtc, $createdBy, $updatedUtc, $updatedBy, 1, 0);
            """, transaction);
        command.With("$id", project.Id.ToString("D"))
            .With("$number", project.Number)
            .With("$name", project.Name)
            .With("$customer", project.Customer)
            .With("$description", project.Description)
            .With("$folderPath", project.FolderPath)
            .With("$status", (int)project.Status)
            .With("$createdUtc", project.CreatedUtc.ToDb())
            .With("$createdBy", project.CreatedBy)
            .With("$updatedUtc", project.UpdatedUtc.ToDb())
            .With("$updatedBy", project.UpdatedBy);
        command.ExecuteNonQuery();
        project.RowVersion = 1;
    }

    public long Update(SqliteConnection connection, SqliteTransaction? transaction, Project project)
    {
        using var command = Db.Command(connection, """
            UPDATE Projects
            SET Number = $number, Name = $name, Customer = $customer, Description = $description,
                FolderPath = $folderPath, Status = $status, UpdatedUtc = $updatedUtc, UpdatedBy = $updatedBy,
                RowVersion = RowVersion + 1
            WHERE Id = $id
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", project.Id.ToString("D"))
            .With("$number", project.Number)
            .With("$name", project.Name)
            .With("$customer", project.Customer)
            .With("$description", project.Description)
            .With("$folderPath", project.FolderPath)
            .With("$status", (int)project.Status)
            .With("$updatedUtc", project.UpdatedUtc.ToDb())
            .With("$updatedBy", project.UpdatedBy);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public long SoftDelete(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, """
            UPDATE Projects
            SET IsDeleted = 1, UpdatedUtc = $updatedUtc, RowVersion = RowVersion + 1
            WHERE Id = $id
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", id.ToString("D")).With("$updatedUtc", DateTime.UtcNow.ToDb());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static Project Map(SqliteDataReader reader) => new()
    {
        Id = reader.Guid(0),
        Number = reader.TextOrNull(1),
        Name = reader.Text(2),
        Customer = reader.TextOrNull(3),
        Description = reader.TextOrNull(4),
        FolderPath = reader.Text(5),
        Status = (ProjectStatus)reader.Int32(6),
        CreatedUtc = reader.Date(7),
        CreatedBy = reader.TextOrNull(8),
        UpdatedUtc = reader.Date(9),
        UpdatedBy = reader.TextOrNull(10),
        RowVersion = reader.Int64(11),
        IsDeleted = reader.Bool(12),
    };
}
