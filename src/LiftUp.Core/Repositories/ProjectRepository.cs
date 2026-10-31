using System.Globalization;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

public sealed class ProjectRepository
{
    private const string Columns =
        "Id, Number, Name, Customer, CustomerStreet, CustomerZip, CustomerCity, Contact, Description, " +
        "ResponsibleUserId, ResponsibleName, FolderPath, Status, ArchivedUtc, ArchivedBy, CreatedUtc, " +
        "CreatedBy, UpdatedUtc, UpdatedBy, RowVersion, IsDeleted, CustomerLine2, Client, ClientStreet, " +
        "ClientLine2, ClientZip, ClientCity";

    public IReadOnlyList<ProjectListItem> Search(SqliteConnection connection, SqliteTransaction? transaction, ProjectQuery query)
    {
        var conditions = new List<string> { "p.IsDeleted = 0" };

        switch (query.Scope)
        {
            case ProjectScope.Active:
                conditions.Add("p.Status <> 3");
                break;
            case ProjectScope.Archived:
                conditions.Add("p.Status = 3");
                break;
        }

        if (query.Status is not null)
        {
            conditions.Add("p.Status = $status");
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            conditions.Add(
                "(p.Name LIKE $pattern OR p.Number LIKE $pattern OR p.Customer LIKE $pattern " +
                "OR p.CustomerStreet LIKE $pattern OR p.CustomerZip LIKE $pattern OR p.CustomerCity LIKE $pattern " +
                "OR p.Client LIKE $pattern OR p.ClientStreet LIKE $pattern OR p.ClientCity LIKE $pattern " +
                "OR p.Contact LIKE $pattern OR p.ResponsibleName LIKE $pattern OR p.Description LIKE $pattern " +
                "OR EXISTS (SELECT 1 FROM ProjectContacts c WHERE c.ProjectId = p.Id AND c.IsDeleted = 0 " +
                "AND (c.Name LIKE $pattern OR c.Email LIKE $pattern OR c.Phone LIKE $pattern " +
                "OR c.Responsibility LIKE $pattern)))");
        }

        var sql = $"""
            SELECT p.Id, p.Number, p.Name, p.Customer, p.Contact, p.ResponsibleName, p.FolderPath, p.Status,
                   p.ArchivedUtc, p.ArchivedBy, p.UpdatedUtc, p.UpdatedBy
            FROM Projects p
            WHERE {string.Join(" AND ", conditions)}
            {OrderBy(query.Sort, query.Descending)};
            """;

        using var command = Db.Command(connection, sql, transaction);
        if (query.Status is not null)
        {
            command.With("$status", (int)query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            command.With("$pattern", $"%{query.Search.Trim()}%");
        }

        using var reader = command.ExecuteReader();
        var items = new List<ProjectListItem>();
        while (reader.Read())
        {
            var folder = reader.Text(6);
            var archived = reader.DateOrNull(8);
            items.Add(new ProjectListItem
            {
                Id = reader.Guid(0),
                Number = reader.Text(1),
                Name = reader.Text(2),
                Customer = reader.Text(3),
                Contact = reader.Text(4),
                Responsible = reader.Text(5),
                FolderPath = folder,
                Status = (ProjectStatus)reader.Int32(7),
                ArchivedText = archived is null
                    ? string.Empty
                    : $"{archived.Value.ToLocalTime():dd.MM.yyyy} ({reader.Text(9)})",
                UpdatedText = reader.Date(10).ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                UpdatedBy = reader.Text(11),
                FolderState = Directory.Exists(folder) ? "vorhanden" : "fehlt",
            });
        }

        return items;
    }

    private static string OrderBy(ProjectSort sort, bool descending)
    {
        var direction = descending ? "DESC" : "ASC";
        var (column, collate) = sort switch
        {
            ProjectSort.Number => ("p.Number", " COLLATE NOCASE"),
            ProjectSort.Customer => ("p.Customer", " COLLATE NOCASE"),
            ProjectSort.Responsible => ("p.ResponsibleName", " COLLATE NOCASE"),
            ProjectSort.Status => ("p.Status", string.Empty),
            ProjectSort.Updated => ("p.UpdatedUtc", string.Empty),
            _ => ("p.Name", " COLLATE NOCASE"),
        };

        return $"ORDER BY {column}{collate} {direction}, p.Name COLLATE NOCASE";
    }

    public Project? GetById(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection,
            $"SELECT {Columns} FROM Projects WHERE Id = $id AND IsDeleted = 0;", transaction);
        command.With("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>
    /// Vergibt die nächste Projektnummer im Format <c>JJJJ-0001</c>. Der Zähler wird
    /// in derselben Transaktion erhöht, damit parallele Anlagen keine Nummer doppelt vergeben.
    /// </summary>
    public string NextNumber(SqliteConnection connection, SqliteTransaction? transaction, int year)
    {
        using var command = Db.Command(connection, """
            INSERT INTO ProjectNumberSequences (Year, LastNumber) VALUES ($year, 1)
            ON CONFLICT(Year) DO UPDATE SET LastNumber = LastNumber + 1
            RETURNING LastNumber;
            """, transaction);
        command.With("$year", year);
        var sequence = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return $"{year}-{sequence:D4}";
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
                (Id, Number, Name, Customer, CustomerStreet, CustomerLine2, CustomerZip, CustomerCity,
                 Client, ClientStreet, ClientLine2, ClientZip, ClientCity, Contact, Description,
                 ResponsibleUserId, ResponsibleName, FolderPath, Status, ArchivedUtc, ArchivedBy, CreatedUtc,
                 CreatedBy, UpdatedUtc, UpdatedBy, RowVersion, IsDeleted)
            VALUES
                ($id, $number, $name, $customer, $customerStreet, $customerLine2, $customerZip, $customerCity,
                 $client, $clientStreet, $clientLine2, $clientZip, $clientCity, $contact,
                 $description, $responsibleId, $responsibleName, $folderPath, $status, $archivedUtc, $archivedBy,
                 $createdUtc, $createdBy, $updatedUtc, $updatedBy, 1, 0);
            """, transaction);
        command.With("$id", project.Id.ToString("D"))
            .With("$number", project.Number)
            .With("$name", project.Name)
            .With("$customer", project.Customer)
            .With("$customerStreet", project.CustomerStreet)
            .With("$customerLine2", project.CustomerLine2)
            .With("$customerZip", project.CustomerZip)
            .With("$customerCity", project.CustomerCity)
            .With("$client", project.Client)
            .With("$clientStreet", project.ClientStreet)
            .With("$clientLine2", project.ClientLine2)
            .With("$clientZip", project.ClientZip)
            .With("$clientCity", project.ClientCity)
            .With("$contact", project.Contact)
            .With("$description", project.Description)
            .With("$responsibleId", project.ResponsibleUserId?.ToString("D"))
            .With("$responsibleName", project.ResponsibleName)
            .With("$folderPath", project.FolderPath)
            .With("$status", (int)project.Status)
            .With("$archivedUtc", project.ArchivedUtc.ToDb())
            .With("$archivedBy", project.ArchivedBy)
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
            SET Number = $number, Name = $name, Customer = $customer, CustomerStreet = $customerStreet,
                CustomerLine2 = $customerLine2, CustomerZip = $customerZip, CustomerCity = $customerCity,
                Client = $client, ClientStreet = $clientStreet, ClientLine2 = $clientLine2,
                ClientZip = $clientZip, ClientCity = $clientCity, Contact = $contact,
                Description = $description, ResponsibleUserId = $responsibleId, ResponsibleName = $responsibleName,
                FolderPath = $folderPath, Status = $status, UpdatedUtc = $updatedUtc, UpdatedBy = $updatedBy,
                RowVersion = RowVersion + 1
            WHERE Id = $id
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", project.Id.ToString("D"))
            .With("$number", project.Number)
            .With("$name", project.Name)
            .With("$customer", project.Customer)
            .With("$customerStreet", project.CustomerStreet)
            .With("$customerLine2", project.CustomerLine2)
            .With("$customerZip", project.CustomerZip)
            .With("$customerCity", project.CustomerCity)
            .With("$client", project.Client)
            .With("$clientStreet", project.ClientStreet)
            .With("$clientLine2", project.ClientLine2)
            .With("$clientZip", project.ClientZip)
            .With("$clientCity", project.ClientCity)
            .With("$contact", project.Contact)
            .With("$description", project.Description)
            .With("$responsibleId", project.ResponsibleUserId?.ToString("D"))
            .With("$responsibleName", project.ResponsibleName)
            .With("$folderPath", project.FolderPath)
            .With("$status", (int)project.Status)
            .With("$updatedUtc", project.UpdatedUtc.ToDb())
            .With("$updatedBy", project.UpdatedBy);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Setzt Archivstatus und Archivstempel, ohne andere Felder anzutasten.</summary>
    public long SetArchiveState(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid id,
        ProjectStatus status,
        DateTime? archivedUtc,
        string? archivedBy,
        string changedBy)
    {
        using var command = Db.Command(connection, """
            UPDATE Projects
            SET Status = $status, ArchivedUtc = $archivedUtc, ArchivedBy = $archivedBy,
                UpdatedUtc = $updatedUtc, UpdatedBy = $updatedBy, RowVersion = RowVersion + 1
            WHERE Id = $id
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", id.ToString("D"))
            .With("$status", (int)status)
            .With("$archivedUtc", archivedUtc.ToDb())
            .With("$archivedBy", archivedBy)
            .With("$updatedUtc", DateTime.UtcNow.ToDb())
            .With("$updatedBy", changedBy);
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

    /// <summary>Ansprechpersonen eines Projekts in der gespeicherten Reihenfolge.</summary>
    public List<ProjectContact> GetContacts(SqliteConnection connection, SqliteTransaction? transaction, Guid projectId)
    {
        using var command = Db.Command(connection, """
            SELECT Id, ProjectId, Name, Email, Phone, Responsibility, SortOrder
            FROM ProjectContacts
            WHERE ProjectId = $projectId AND IsDeleted = 0
            ORDER BY SortOrder, Name COLLATE NOCASE;
            """, transaction);
        command.With("$projectId", projectId.ToString("D"));

        using var reader = command.ExecuteReader();
        var contacts = new List<ProjectContact>();
        while (reader.Read())
        {
            contacts.Add(new ProjectContact
            {
                Id = reader.Guid(0),
                ProjectId = reader.Guid(1),
                Name = reader.Text(2),
                Email = reader.TextOrNull(3),
                Phone = reader.TextOrNull(4),
                Responsibility = reader.TextOrNull(5),
                SortOrder = reader.Int32(6),
            });
        }

        return contacts;
    }

    /// <summary>
    /// Schreibt die Ansprechpersonen neu. Bisherige Zeilen werden weich gelöscht, damit ein
    /// späterer Abgleich die Änderung nachvollziehen kann.
    /// </summary>
    public List<ProjectContact> ReplaceContacts(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid projectId,
        IReadOnlyList<ProjectContactInput> contacts,
        string changedBy)
    {
        using (var retire = Db.Command(connection, """
            UPDATE ProjectContacts
            SET IsDeleted = 1, RowVersion = RowVersion + 1
            WHERE ProjectId = $projectId AND IsDeleted = 0;
            """, transaction))
        {
            retire.With("$projectId", projectId.ToString("D"));
            retire.ExecuteNonQuery();
        }

        var now = DateTime.UtcNow;
        var saved = new List<ProjectContact>();

        for (var index = 0; index < contacts.Count; index++)
        {
            var input = contacts[index];
            var contact = new ProjectContact
            {
                ProjectId = projectId,
                Name = input.Name,
                Email = input.Email,
                Phone = input.Phone,
                Responsibility = input.Responsibility,
                SortOrder = index,
            };

            using var insert = Db.Command(connection, """
                INSERT INTO ProjectContacts
                    (Id, ProjectId, Name, Email, Phone, Responsibility, SortOrder, CreatedUtc, CreatedBy,
                     RowVersion, IsDeleted)
                VALUES ($id, $projectId, $name, $email, $phone, $responsibility, $sortOrder,
                        $createdUtc, $createdBy, 1, 0);
                """, transaction);
            insert.With("$id", contact.Id.ToString("D"))
                .With("$projectId", projectId.ToString("D"))
                .With("$name", contact.Name)
                .With("$email", contact.Email)
                .With("$phone", contact.Phone)
                .With("$responsibility", contact.Responsibility)
                .With("$sortOrder", contact.SortOrder)
                .With("$createdUtc", now.ToDb())
                .With("$createdBy", changedBy);
            insert.ExecuteNonQuery();
            saved.Add(contact);
        }

        return saved;
    }

    private static Project Map(SqliteDataReader reader) => new()
    {
        Id = reader.Guid(0),
        Number = reader.TextOrNull(1),
        Name = reader.Text(2),
        Customer = reader.TextOrNull(3),
        CustomerStreet = reader.TextOrNull(4),
        CustomerZip = reader.TextOrNull(5),
        CustomerCity = reader.TextOrNull(6),
        Contact = reader.TextOrNull(7),
        Description = reader.TextOrNull(8),
        ResponsibleUserId = reader.TextOrNull(9) is { } responsibleId ? Guid.Parse(responsibleId) : null,
        ResponsibleName = reader.TextOrNull(10),
        FolderPath = reader.Text(11),
        Status = (ProjectStatus)reader.Int32(12),
        ArchivedUtc = reader.DateOrNull(13),
        ArchivedBy = reader.TextOrNull(14),
        CreatedUtc = reader.Date(15),
        CreatedBy = reader.TextOrNull(16),
        UpdatedUtc = reader.Date(17),
        UpdatedBy = reader.TextOrNull(18),
        RowVersion = reader.Int64(19),
        IsDeleted = reader.Bool(20),
        CustomerLine2 = reader.TextOrNull(21),
        Client = reader.TextOrNull(22),
        ClientStreet = reader.TextOrNull(23),
        ClientLine2 = reader.TextOrNull(24),
        ClientZip = reader.TextOrNull(25),
        ClientCity = reader.TextOrNull(26),
    };
}
