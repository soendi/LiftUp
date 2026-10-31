namespace LiftUp.Core.Domain;

public enum ProjectStatus
{
    Active = 0,
    Paused = 1,
    Completed = 2,
    Archived = 3,
}

public sealed record ProjectStatusInfo(ProjectStatus Value, string Name);

public static class ProjectStatusCatalog
{
    public static IReadOnlyList<ProjectStatusInfo> All { get; } =
    [
        new(ProjectStatus.Active, "In Arbeit"),
        new(ProjectStatus.Paused, "Pausiert"),
        new(ProjectStatus.Completed, "Abgeschlossen"),
        new(ProjectStatus.Archived, "Archiviert"),
    ];

    /// <summary>Status, die in der normalen Projektübersicht wählbar sind (ohne Archiv).</summary>
    public static IReadOnlyList<ProjectStatusInfo> Selectable { get; } =
        All.Where(info => info.Value != ProjectStatus.Archived).ToArray();

    public static string Describe(ProjectStatus status) =>
        All.FirstOrDefault(info => info.Value == status)?.Name ?? status.ToString();
}

/// <summary>Unterordner, die in jedem Projektordner angelegt werden.</summary>
public static class ProjectFolders
{
    public static IReadOnlyList<string> Standard { get; } =
    [
        "01_Dokumente",
        "02_Bilder",
        "03_PDF",
        "04_Vorlagen",
    ];
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Projektnummer, eindeutig; wird beim Anlegen automatisch vergeben.</summary>
    public string? Number { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Customer { get; set; }

    /// <summary>Straße und Hausnummer des Kunden.</summary>
    public string? CustomerStreet { get; set; }

    /// <summary>Postleitzahl des Kunden.</summary>
    public string? CustomerZip { get; set; }

    /// <summary>Ort des Kunden.</summary>
    public string? CustomerCity { get; set; }

    /// <summary>
    /// Name der ersten Ansprechperson. Wird beim Speichern aus <see cref="Contacts"/> gepflegt,
    /// damit Übersicht, Suche und ältere Vorlagen weiter funktionieren.
    /// </summary>
    public string? Contact { get; set; }

    /// <summary>Ansprechpersonen beim Kunden, in der eingegebenen Reihenfolge.</summary>
    public List<ProjectContact> Contacts { get; set; } = [];

    /// <summary>Anschrift in einer Zeile, z. B. „Hauptstraße 12, 12345 Musterstadt“.</summary>
    public string CustomerAddress
    {
        get
        {
            var city = string.Join(" ", new[] { CustomerZip, CustomerCity }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            return string.Join(", ", new[] { CustomerStreet, city }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public string? Description { get; set; }

    /// <summary>Verantwortlicher Mitarbeiter.</summary>
    public Guid? ResponsibleUserId { get; set; }

    /// <summary>Name des Verantwortlichen zum Zeitpunkt der Zuweisung (denormalisiert).</summary>
    public string? ResponsibleName { get; set; }

    /// <summary>Absoluter Pfad des Projektordners, kann auf jedem Laufwerk liegen.</summary>
    public string FolderPath { get; set; } = string.Empty;

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    /// <summary>Zeitpunkt der Archivierung; bleibt beim Wiederherstellen erhalten? Nein: wird zurückgesetzt.</summary>
    public DateTime? ArchivedUtc { get; set; }

    public string? ArchivedBy { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }

    public long RowVersion { get; set; } = 1;
    public bool IsDeleted { get; set; }

    public bool IsArchived => Status == ProjectStatus.Archived;
}

/// <summary>Eingabedaten aus dem Projektdialog.</summary>
public sealed class ProjectInput
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Leer lassen, damit automatisch eine Nummer vergeben wird.</summary>
    public string? Number { get; set; }

    public string? Customer { get; set; }
    public string? CustomerStreet { get; set; }
    public string? CustomerZip { get; set; }
    public string? CustomerCity { get; set; }

    /// <summary>Ansprechpersonen; leere Zeilen werden beim Speichern ignoriert.</summary>
    public IReadOnlyList<ProjectContactInput> Contacts { get; set; } = [];

    /// <summary>Name der ersten Ansprechperson, abgeleitet aus <see cref="Contacts"/>.</summary>
    public string? PrimaryContact => Contacts.Count > 0 ? Contacts[0].Name : null;

    public string? Description { get; set; }
    public Guid? ResponsibleUserId { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    /// <summary>Ordner samt Unterordnern anlegen, wenn er noch nicht existiert.</summary>
    public bool CreateFolderIfMissing { get; set; } = true;
}

/// <summary>Ansprechperson beim Kunden.</summary>
public sealed class ProjectContact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>Zuständigkeit, z. B. Bauleitung, Buchhaltung, Technik.</summary>
    public string? Responsibility { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Einzeilige Darstellung für Listen und Platzhalter.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Name };
            if (!string.IsNullOrWhiteSpace(Responsibility)) parts.Add(Responsibility);
            if (!string.IsNullOrWhiteSpace(Phone)) parts.Add(Phone);
            if (!string.IsNullOrWhiteSpace(Email)) parts.Add(Email);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Eingabe einer Ansprechperson aus dem Projektdialog.</summary>
public sealed class ProjectContactInput
{
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Responsibility { get; set; }
}

public enum ProjectSort
{
    Name,
    Number,
    Customer,
    Status,
    Updated,
    Responsible,
}

/// <summary>Welche Projekte die Übersicht zeigt.</summary>
public enum ProjectScope
{
    /// <summary>Alle nicht archivierten Projekte (Normalfall).</summary>
    Active,
    Archived,
    All,
}

public sealed class ProjectQuery
{
    public string? Search { get; init; }
    public ProjectStatus? Status { get; init; }
    public ProjectScope Scope { get; init; } = ProjectScope.Active;
    public ProjectSort Sort { get; init; } = ProjectSort.Name;
    public bool Descending { get; init; }
}

public sealed class ProjectListItem
{
    public Guid Id { get; init; }
    public string Number { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Customer { get; init; } = string.Empty;
    public string Contact { get; init; } = string.Empty;
    public string Responsible { get; init; } = string.Empty;
    public string FolderPath { get; init; } = string.Empty;
    public ProjectStatus Status { get; init; }
    public string StatusText => ProjectStatusCatalog.Describe(Status);
    public bool IsArchived => Status == ProjectStatus.Archived;
    public string ArchivedText { get; init; } = string.Empty;
    public string UpdatedText { get; init; } = string.Empty;
    public string UpdatedBy { get; init; } = string.Empty;
    public string FolderState { get; init; } = string.Empty;
}
