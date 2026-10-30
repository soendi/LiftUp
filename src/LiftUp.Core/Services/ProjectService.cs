using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;

namespace LiftUp.Core.Services;

/// <summary>
/// Projektverwaltung inklusive Projektordner. Gelöscht wird weich (IsDeleted),
/// damit der spätere Abgleich mit PostgreSQL Löschungen nachvollziehen kann.
/// </summary>
public sealed class ProjectService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly ProjectRepository _projects = new();
    private readonly ChangeLogWriter _changes;

    public ProjectService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database);
    }

    public IReadOnlyList<ProjectListItem> Search(string? search)
    {
        _actor.Require(Permission.ViewProjects);
        using var connection = _database.Open();
        return _projects.Search(connection, null, search);
    }

    public Project GetProject(Guid id)
    {
        _actor.Require(Permission.ViewProjects);
        using var connection = _database.Open();
        return _projects.GetById(connection, null, id)
               ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");
    }

    public Project Create(ProjectInput input)
    {
        _actor.Require(Permission.CreateProjects);
        ArgumentNullException.ThrowIfNull(input);

        var prepared = Prepare(input, existingProjectId: null);
        var now = DateTime.UtcNow;

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        EnsureUnique(connection, transaction, prepared, existingProjectId: null);

        var project = new Project
        {
            Name = prepared.Name,
            Number = prepared.Number,
            Customer = prepared.Customer,
            Description = prepared.Description,
            FolderPath = prepared.FolderPath,
            Status = prepared.Status,
            CreatedUtc = now,
            CreatedBy = _actor.UserName,
            UpdatedUtc = now,
            UpdatedBy = _actor.UserName,
        };

        _projects.Insert(connection, transaction, project);
        _changes.Record(connection, transaction, "Project", project.Id.ToString("D"), "Create",
            new
            {
                project.Name,
                project.Number,
                project.Customer,
                project.Description,
                project.FolderPath,
                Status = (int)project.Status,
            },
            project.RowVersion);
        transaction.Commit();

        // Ordner erst nach dem erfolgreichen Schreiben anlegen: dann bleibt keine
        // verwaiste Ordnerstruktur zurück, wenn die Datenbank ablehnt.
        if (prepared.CreateFolderIfMissing)
        {
            FolderService.EnsureCreated(project.FolderPath);
        }

        return project;
    }

    public Project Update(Guid id, ProjectInput input)
    {
        _actor.Require(Permission.EditProjects);
        ArgumentNullException.ThrowIfNull(input);

        var prepared = Prepare(input, id);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, id)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        EnsureUnique(connection, transaction, prepared, id);

        project.Name = prepared.Name;
        project.Number = prepared.Number;
        project.Customer = prepared.Customer;
        project.Description = prepared.Description;
        project.FolderPath = prepared.FolderPath;
        project.Status = prepared.Status;
        project.UpdatedUtc = DateTime.UtcNow;
        project.UpdatedBy = _actor.UserName;

        var rowVersion = _projects.Update(connection, transaction, project);
        _changes.Record(connection, transaction, "Project", project.Id.ToString("D"), "Update",
            new
            {
                project.Name,
                project.Number,
                project.Customer,
                project.Description,
                project.FolderPath,
                Status = (int)project.Status,
            },
            rowVersion);
        transaction.Commit();

        if (prepared.CreateFolderIfMissing)
        {
            FolderService.EnsureCreated(project.FolderPath);
        }

        return project;
    }

    /// <summary>
    /// Entfernt das Projekt. Optional wird der Projektordner in den Papierkorb gelegt;
    /// das muss der Benutzer im Dialog ausdrücklich wählen.
    /// </summary>
    public string? Delete(Guid id, bool moveFolderToRecycleBin)
    {
        _actor.Require(Permission.DeleteProjects);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, id)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        var rowVersion = _projects.SoftDelete(connection, transaction, id);
        _changes.Record(connection, transaction, "Project", id.ToString("D"), "Delete",
            new { project.Name, project.Number, project.FolderPath }, rowVersion);
        transaction.Commit();

        if (!moveFolderToRecycleBin)
        {
            return null;
        }

        return FolderService.TryMoveToRecycleBin(project.FolderPath, out var error)
            ? null
            : error ?? "Der Ordner konnte nicht in den Papierkorb verschoben werden.";
    }

    private ProjectInput Prepare(ProjectInput input, Guid? existingProjectId)
    {
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ValidationException("Bitte einen Projektnamen angeben.");
        }

        var folderPath = FolderService.Normalize(input.FolderPath, out var error);
        if (error is not null)
        {
            throw new ValidationException(error);
        }

        if (input.CreateFolderIfMissing)
        {
            if (File.Exists(folderPath))
            {
                throw new ValidationException("Der Pfad zeigt auf eine Datei, nicht auf einen Ordner.");
            }
        }
        else if (!Directory.Exists(folderPath))
        {
            throw new ValidationException("Der Projektordner existiert nicht. Bitte Ordner anlegen lassen oder einen vorhandenen wählen.");
        }

        var number = string.IsNullOrWhiteSpace(input.Number) ? null : input.Number.Trim();
        var customer = string.IsNullOrWhiteSpace(input.Customer) ? null : input.Customer.Trim();
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();

        return new ProjectInput
        {
            Name = name,
            Number = number,
            Customer = customer,
            Description = description,
            FolderPath = folderPath,
            Status = input.Status,
            CreateFolderIfMissing = input.CreateFolderIfMissing,
        };
    }

    private void EnsureUnique(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        ProjectInput prepared,
        Guid? existingProjectId)
    {
        if (prepared.Number is not null &&
            _projects.NumberExists(connection, transaction, prepared.Number, existingProjectId))
        {
            throw new ValidationException($"Die Projektnummer \"{prepared.Number}\" ist bereits vergeben.");
        }

        if (_projects.FolderExists(connection, transaction, prepared.FolderPath, existingProjectId))
        {
            throw new ValidationException("Dieser Projektordner ist bereits einem anderen Projekt zugeordnet.");
        }
    }
}
