using LiftUp.Core.Data;
using LiftUp.Core.Documents;
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
        var contacts = _projects.GetContacts(connection, null, id);
        project.Contacts = contacts.Where(contact => contact.Kind == ProjectContactKind.Customer).ToList();
        project.ClientContacts = contacts.Where(contact => contact.Kind == ProjectContactKind.Client).ToList();
        project.Milestones = _projects.GetMilestones(connection, null, id);
        return project;
    }

    /// <summary>
    /// Schreibt nur die Benennung der beiden Varianten der Investitionsplanung. Sie erscheint
    /// als Kopf „Variante 1 – …“ in der Tabelle und im PDF.
    /// </summary>
    public Project SaveVariantLabels(Guid projectId, string? modernisierung, string? ersatzanlage)
    {
        _actor.Require(Permission.EditProjects);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        project.Variant1Label = (modernisierung ?? string.Empty).Trim();
        project.Variant2Label = (ersatzanlage ?? string.Empty).Trim();
        project.RowVersion = _projects.UpdateVariantLabels(
            connection, transaction, projectId, project.Variant1Label, project.Variant2Label, _actor.UserName);

        _changes.Record(connection, transaction, "Project", projectId.ToString("D"), "Variants",
            new { project.Variant1Label, project.Variant2Label }, project.RowVersion);
        transaction.Commit();

        return project;
    }

    /// <summary>
    /// Schreibt nur die Meilensteine eines Projekts – für die Projektübersicht, in der sie
    /// direkt gepflegt werden. Die Begehung bleibt dabei so fest wie im Projektdialog.
    /// </summary>
    public Project SaveMilestones(Guid projectId, IReadOnlyList<ProjectMilestoneInput> milestones)
    {
        _actor.Require(Permission.EditProjects);
        ArgumentNullException.ThrowIfNull(milestones);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        var prepared = PrepareMilestones(milestones);
        project.Milestones = _projects.ReplaceMilestones(
            connection, transaction, projectId, prepared, _actor.UserName);

        _changes.Record(connection, transaction, "Project", projectId.ToString("D"), "Milestones",
            new { Milestones = prepared.Count, project.Name },
            project.RowVersion);

        transaction.Commit();
        return project;
    }

    public Project Create(ProjectInput input)
    {
        _actor.Require(Permission.CreateProjects);
        ArgumentNullException.ThrowIfNull(input);

        var (prepared, imageSource) = Prepare(input);
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
            ImagePath = prepared.ImagePath,
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
        SaveContacts(connection, transaction, project, prepared);
        SaveMilestones(connection, transaction, project, prepared);
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
                ClientContacts = prepared.ClientContacts.Count,
                Milestones = prepared.Milestones.Count,
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

        CopyProjectImage(project, imageSource);

        return project;
    }

    public Project Update(Guid id, ProjectInput input)
    {
        _actor.Require(Permission.EditProjects);
        ArgumentNullException.ThrowIfNull(input);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, id)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        // Das bisherige Bild bleibt gültig, auch wenn die Datei gerade fehlt.
        var (prepared, imageSource) = Prepare(input, project.ImagePath);

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
        project.ImagePath = prepared.ImagePath;
        project.Status = prepared.Status;
        project.UpdatedUtc = DateTime.UtcNow;
        project.UpdatedBy = _actor.UserName;

        var rowVersion = _projects.Update(connection, transaction, project);
        SaveContacts(connection, transaction, project, prepared);
        SaveMilestones(connection, transaction, project, prepared);
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
                ClientContacts = prepared.ClientContacts.Count,
                Milestones = prepared.Milestones.Count,
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

        CopyProjectImage(project, imageSource);

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

    private (ProjectInput Prepared, string? ImageSource) Prepare(ProjectInput input, string? currentImagePath = null)
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

        // Gewähltes Bild: Die Quelldatei wird gemerkt, im Eingabesatz steht der Zielpfad.
        var imageSource = Trimmed(input.ImagePath);

        var prepared = new ProjectInput
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
            Contacts = PrepareContacts(input.Contacts, ProjectContactKind.Customer),
            ClientContacts = PrepareContacts(input.ClientContacts, ProjectContactKind.Client),
            Milestones = PrepareMilestones(input.Milestones),
            Description = Trimmed(input.Description),
            ResponsibleUserId = input.ResponsibleUserId,
            FolderPath = folderPath,
            ImagePath = PrepareProjectImage(imageSource, folderPath, currentImagePath),
            Status = input.Status,
            CreateFolderIfMissing = input.CreateFolderIfMissing,
        };

        return (prepared, imageSource);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Entfernt leere Zeilen und verlangt einen Namen je Ansprechperson: eine Zeile nur mit
    /// Telefonnummer wäre im Dokument niemandem zuzuordnen.
    /// </summary>
    private static IReadOnlyList<ProjectContactInput> PrepareContacts(
        IReadOnlyList<ProjectContactInput>? contacts,
        ProjectContactKind kind)
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
                Kind = kind,
                Name = name,
                Email = email,
                Phone = phone,
                Responsibility = responsibility,
            });
        }

        return prepared;
    }

    /// <summary>
    /// Schreibt Kunden- und Auftraggeber-Ansprechpersonen gemeinsam und verteilt sie danach
    /// wieder auf die beiden Listen des Projekts.
    /// </summary>
    private void SaveContacts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Project project,
        ProjectInput prepared)
    {
        var saved = _projects.ReplaceContacts(
            connection,
            transaction,
            project.Id,
            [.. prepared.Contacts, .. prepared.ClientContacts],
            _actor.UserName);

        project.Contacts = saved.Where(contact => contact.Kind == ProjectContactKind.Customer).ToList();
        project.ClientContacts = saved.Where(contact => contact.Kind == ProjectContactKind.Client).ToList();
    }

    /// <summary>
    /// Entfernt leere Zeilen und verlangt eine Bezeichnung je Meilenstein. Ein abgehakter
    /// Meilenstein ohne Datum bekommt das heutige. Die Begehung ist fest: Sie fehlt nie und
    /// behält ihren Namen, auch wenn ein anderer Titel oder gar keine Zeile ankommt.
    /// </summary>
    private static IReadOnlyList<ProjectMilestoneInput> PrepareMilestones(
        IReadOnlyList<ProjectMilestoneInput>? milestones)
    {
        var prepared = new List<ProjectMilestoneInput>();
        var hasInspection = false;

        foreach (var milestone in milestones ?? [])
        {
            var title = (milestone.Title ?? string.Empty).Trim();
            if (title.Length == 0 && !milestone.IsDone && milestone.DoneDate is null)
            {
                continue;
            }

            if (title.Length == 0)
            {
                throw new ValidationException("Bitte zu jedem Meilenstein eine Bezeichnung angeben.");
            }

            if (title.Contains(ProjectMilestoneCatalog.Inspection, StringComparison.OrdinalIgnoreCase))
            {
                title = ProjectMilestoneCatalog.Inspection;
                hasInspection = true;
            }

            prepared.Add(new ProjectMilestoneInput
            {
                Title = title,
                IsDone = milestone.IsDone,
                DoneDate = milestone.DoneDate?.Date ?? (milestone.IsDone ? DateTime.Today : null),
            });
        }

        if (!hasInspection)
        {
            prepared.Insert(0, new ProjectMilestoneInput { Title = ProjectMilestoneCatalog.Inspection });
        }

        return prepared;
    }

    /// <summary>Schreibt die Meilensteine des Projekts.</summary>
    private void SaveMilestones(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Project project,
        ProjectInput prepared)
    {
        project.Milestones = _projects.ReplaceMilestones(
            connection, transaction, project.Id, prepared.Milestones, _actor.UserName);
    }

    /// <summary>
    /// Prüft das gewählte Projektbild und bestimmt seinen Platz im Projektordner. Liegt die
    /// Datei bereits im Bilderordner, bleibt ihr Pfad; sonst wird sie beim Speichern dorthin
    /// kopiert (siehe <see cref="CopyProjectImage"/>).
    /// </summary>
    private static string? PrepareProjectImage(string? path, string projectFolder, string? currentImagePath)
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
            throw new ValidationException("Der Pfad zum Projektbild ist ungültig.");
        }

        if (!File.Exists(fullPath))
        {
            // Ein schon gespeichertes Bild bleibt eingetragen, auch wenn die Datei gerade fehlt.
            if (currentImagePath is not null
                && string.Equals(fullPath, currentImagePath, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            throw new ValidationException("Die gewählte Bilddatei wurde nicht gefunden.");
        }

        if (ImageEmbedding.ContentTypeFor(fullPath) is null)
        {
            throw new ValidationException(
                $"Der Dateityp \"{Path.GetExtension(fullPath)}\" wird für das Projektbild nicht unterstützt. " +
                "Möglich sind PNG, JPG, GIF, WebP, BMP, SVG und TIFF.");
        }

        var imageFolder = Path.Combine(projectFolder, ProjectFolders.Images);
        return IsInsideFolder(fullPath, imageFolder)
            ? fullPath
            : UniqueImagePath(imageFolder, Path.GetFileName(fullPath));
    }

    /// <summary>Liegt die Datei schon im angegebenen Ordner?</summary>
    private static bool IsInsideFolder(string filePath, string folder)
    {
        var fullFolder = Path.GetFullPath(folder);
        if (!fullFolder.EndsWith(Path.DirectorySeparatorChar))
        {
            fullFolder += Path.DirectorySeparatorChar;
        }

        return filePath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Freier Dateiname im Bilderordner: ein gleichnamiges Bild wird nicht überschrieben,
    /// sondern bekommt „(2)“ angehängt.
    /// </summary>
    private static string UniqueImagePath(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        if (!File.Exists(target))
        {
            return target;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 1000; index++)
        {
            target = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!File.Exists(target))
            {
                return target;
            }
        }

        return Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// Kopiert das gewählte Bild nach dem Speichern in den Projektordner. Liegt es dort schon,
    /// bleibt die Datei unangetastet.
    /// </summary>
    private static void CopyProjectImage(Project project, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(project.ImagePath))
        {
            return;
        }

        if (string.Equals(sourcePath, project.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var folder = Path.GetDirectoryName(project.ImagePath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.Copy(sourcePath, project.ImagePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LiftUpException(
                "Das Projekt wurde gespeichert, das Bild konnte aber nicht in den Projektordner kopiert " +
                $"werden: {exception.Message}");
        }
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
