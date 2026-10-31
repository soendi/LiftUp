using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Services;

/// <summary>
/// Projektverwaltung inklusive Projektordner und Archiv. Gelöscht wird weich (IsDeleted),
/// damit der spätere Abgleich mit der zentralen Datenbank Löschungen nachvollziehen kann.
/// </summary>
public sealed class ProjectService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly ProjectRepository _projects = new();
    private readonly UserRepository _users = new();
    private readonly ChangeLogWriter _changes;

    public ProjectService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database, _actor.UserName);
    }

    /// <summary>Projektübersicht; ohne Archivbereich sind archivierte Projekte ausgeblendet.</summary>
    public IReadOnlyList<ProjectListItem> Search(ProjectQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        _actor.Require(Permission.ViewProjects);
        if (query.Scope != ProjectScope.Active)
        {
            _actor.Require(Permission.ViewArchivedProjects);
        }

        using var connection = _database.Open();
        return _projects.Search(connection, null, query);
    }

    /// <summary>Auswählbare Verantwortliche für den Projektdialog.</summary>
    public IReadOnlyList<UserListItem> GetResponsibleChoices()
    {
        _actor.Require(Permission.ViewProjects);
        using var connection = _database.Open();
        return _users.GetListItems(connection, null, null).Where(user => user.IsActive).ToArray();
    }

    public Project GetProject(Guid id)
    {
        _actor.Require(Permission.ViewProjects);
        using var connection = _database.Open();
        var project = _projects.GetById(connection, null, id)
               ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");
        project.Contacts = _projects.GetContacts(connection, null, id);
        return project;
    }

    public Project Create(ProjectInput input)
    {
        _actor.Require(Permission.CreateProjects);
        ArgumentNullException.ThrowIfNull(input);

        var prepared = Prepare(input);
        var now = DateTime.UtcNow;

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        // Automatische Nummern vergibt die Datenbank selbst; eine vorgegebene Nummer
        // muss eindeutig sein.
        var number = prepared.Number ?? _projects.NextNumber(connection, transaction, now.Year);
        var responsibleName = ResolveResponsibleName(connection, transaction, prepared.ResponsibleUserId);

        EnsureUnique(connection, transaction, prepared, number, existingProjectId: null);

        var project = new Project
        {
            Name = prepared.Name,
            Number = number,
            Customer = prepared.Customer,
            CustomerStreet = prepared.CustomerStreet,
            CustomerLine2 = prepared.CustomerLine2,
            CustomerZip = prepared.CustomerZip,
            CustomerCity = prepared.CustomerCity,
            Client = prepared.Client,
            ClientStreet = prepared.ClientStreet,
            ClientLine2 = prepared.ClientLine2,
            ClientZip = prepared.ClientZip,
            ClientCity = prepared.ClientCity,
            Contact = prepared.PrimaryContact,
            Description = prepared.Description,
            ResponsibleUserId = prepared.ResponsibleUserId,
            ResponsibleName = responsibleName,
            FolderPath = prepared.FolderPath,
            Status = prepared.Status,
            CreatedUtc = now,
            CreatedBy = _actor.UserName,
            UpdatedUtc = now,
            UpdatedBy = _actor.UserName,
        };

        if (project.Status == ProjectStatus.Archived)
        {
            project.ArchivedUtc = now;
            project.ArchivedBy = _actor.UserName;
        }

        _projects.Insert(connection, transaction, project);
        project.Contacts = _projects.ReplaceContacts(
            connection, transaction, project.Id, prepared.Contacts, _actor.UserName);
        _changes.Record(connection, transaction, "Project", project.Id.ToString("D"), "Create",
            new
            {
                project.Name,
                project.Number,
                project.Customer,
                CustomerStreet = project.CustomerStreet,
                CustomerCity = project.CustomerCity,
                Contact = project.Contact,
                Contacts = prepared.Contacts.Count,
                project.ResponsibleName,
                project.FolderPath,
                Status = (int)project.Status,
            },
            project.RowVersion);
        transaction.Commit();

        // Ordner erst nach dem erfolgreichen Schreiben anlegen: dann bleibt keine
        // verwaiste Ordnerstruktur zurück, wenn die Datenbank ablehnt.
        if (prepared.CreateFolderIfMissing)
        {
            FolderService.EnsureStandardStructure(project.FolderPath);
        }

        return project;
    }

    public Project Update(Guid id, ProjectInput input)
    {
        _actor.Require(Permission.EditProjects);
        ArgumentNullException.ThrowIfNull(input);

        var prepared = Prepare(input);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, id)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        var number = prepared.Number ?? project.Number
                     ?? _projects.NextNumber(connection, transaction, DateTime.UtcNow.Year);

        EnsureUnique(connection, transaction, prepared, number, id);

        project.Name = prepared.Name;
        project.Number = number;
        project.Customer = prepared.Customer;
        project.CustomerStreet = prepared.CustomerStreet;
        project.CustomerLine2 = prepared.CustomerLine2;
        project.CustomerZip = prepared.CustomerZip;
        project.CustomerCity = prepared.CustomerCity;
        project.Client = prepared.Client;
        project.ClientStreet = prepared.ClientStreet;
        project.ClientLine2 = prepared.ClientLine2;
        project.ClientZip = prepared.ClientZip;
        project.ClientCity = prepared.ClientCity;
        project.Contact = prepared.PrimaryContact;
        project.Description = prepared.Description;
        project.ResponsibleUserId = prepared.ResponsibleUserId;
        project.ResponsibleName = ResolveResponsibleName(connection, transaction, prepared.ResponsibleUserId);
        project.FolderPath = prepared.FolderPath;
        project.Status = prepared.Status;
        project.UpdatedUtc = DateTime.UtcNow;
        project.UpdatedBy = _actor.UserName;

        var rowVersion = _projects.Update(connection, transaction, project);
        project.Contacts = _projects.ReplaceContacts(
            connection, transaction, project.Id, prepared.Contacts, _actor.UserName);
        _changes.Record(connection, transaction, "Project", project.Id.ToString("D"), "Update",
            new
            {
                project.Name,
                project.Number,
                project.Customer,
                CustomerStreet = project.CustomerStreet,
                CustomerCity = project.CustomerCity,
                Contact = project.Contact,
                Contacts = prepared.Contacts.Count,
                project.ResponsibleName,
                project.FolderPath,
                Status = (int)project.Status,
            },
            rowVersion);
        transaction.Commit();

        if (prepared.CreateFolderIfMissing)
        {
            FolderService.EnsureStandardStructure(project.FolderPath);
        }

        return project;
    }

    /// <summary>
    /// Archiviert ein Projekt: nur der Status ändert sich, Dateien und Ordner bleiben
    /// unberührt. Zeitpunkt und Benutzer werden festgehalten.
    /// </summary>
    public void Archive(Guid id) => SetArchiveState(id, archive: true);

    public void Restore(Guid id) => SetArchiveState(id, archive: false);

    private void SetArchiveState(Guid id, bool archive)
    {
        _actor.Require(Permission.ArchiveProjects);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, id)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        if (archive && project.Status == ProjectStatus.Archived)
        {
            throw new ValidationException("Das Projekt ist bereits archiviert.");
        }

        if (!archive && project.Status != ProjectStatus.Archived)
        {
            throw new ValidationException("Das Projekt ist nicht archiviert.");
        }

        var now = DateTime.UtcNow;
        var status = archive ? ProjectStatus.Archived : ProjectStatus.Active;
        var rowVersion = _projects.SetArchiveState(
            connection,
            transaction,
            id,
            status,
            archive ? now : null,
            archive ? _actor.UserName : null,
            _actor.UserName);

        _changes.Record(connection, transaction, "Project", id.ToString("D"), archive ? "Archive" : "Restore",
            new
            {
                project.Name,
                project.Number,
                Status = (int)status,
                ArchivedBy = archive ? _actor.UserName : null,
            },
            rowVersion);
        transaction.Commit();
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

    private ProjectInput Prepare(ProjectInput input)
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

        return new ProjectInput
        {
            Name = name,
            Number = string.IsNullOrWhiteSpace(input.Number) ? null : input.Number.Trim(),
            Customer = Trimmed(input.Customer),
            CustomerStreet = Trimmed(input.CustomerStreet),
            CustomerLine2 = Trimmed(input.CustomerLine2),
            CustomerZip = Trimmed(input.CustomerZip),
            CustomerCity = Trimmed(input.CustomerCity),
            Client = Trimmed(input.Client),
            ClientStreet = Trimmed(input.ClientStreet),
            ClientLine2 = Trimmed(input.ClientLine2),
            ClientZip = Trimmed(input.ClientZip),
            ClientCity = Trimmed(input.ClientCity),
            Contacts = PrepareContacts(input.Contacts),
            Description = Trimmed(input.Description),
            ResponsibleUserId = input.ResponsibleUserId,
            FolderPath = folderPath,
            Status = input.Status,
            CreateFolderIfMissing = input.CreateFolderIfMissing,
        };
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Entfernt leere Zeilen und verlangt einen Namen je Ansprechperson: eine Zeile nur mit
    /// Telefonnummer wäre im Dokument niemandem zuzuordnen.
    /// </summary>
    private static IReadOnlyList<ProjectContactInput> PrepareContacts(IReadOnlyList<ProjectContactInput>? contacts)
    {
        var prepared = new List<ProjectContactInput>();

        foreach (var contact in contacts ?? [])
        {
            var name = (contact.Name ?? string.Empty).Trim();
            var email = Trimmed(contact.Email);
            var phone = Trimmed(contact.Phone);
            var responsibility = Trimmed(contact.Responsibility);

            if (name.Length == 0 && email is null && phone is null && responsibility is null)
            {
                continue;
            }

            if (name.Length == 0)
            {
                throw new ValidationException("Bitte zu jeder Ansprechperson einen Namen angeben.");
            }

            prepared.Add(new ProjectContactInput
            {
                Name = name,
                Email = email,
                Phone = phone,
                Responsibility = responsibility,
            });
        }

        return prepared;
    }

    private string? ResolveResponsibleName(SqliteConnection connection, SqliteTransaction? transaction, Guid? userId)
    {
        if (userId is null)
        {
            return null;
        }

        var user = _users.GetById(connection, transaction, userId.Value)
                   ?? throw new ValidationException("Der gewählte Verantwortliche wurde nicht gefunden.");

        return string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;
    }

    private void EnsureUnique(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProjectInput prepared,
        string number,
        Guid? existingProjectId)
    {
        if (_projects.NumberExists(connection, transaction, number, existingProjectId))
        {
            throw new ValidationException($"Die Projektnummer \"{number}\" ist bereits vergeben.");
        }

        if (_projects.FolderExists(connection, transaction, prepared.FolderPath, existingProjectId))
        {
            throw new ValidationException("Dieser Projektordner ist bereits einem anderen Projekt zugeordnet.");
        }
    }
}
