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
    /// <summary>Unterordner für die Bilder eines Projekts; hier landet auch das Projektbild.</summary>
    public const string Images = "02_Bilder";

    public static IReadOnlyList<string> Standard { get; } =
    [
        "01_Dokumente",
        Images,
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

    /// <summary>Zweite Adresszeile des Kunden, z. B. Gebäude, Abteilung oder Postfach.</summary>
    public string? CustomerLine2 { get; set; }

    /// <summary>Postleitzahl des Kunden.</summary>
    public string? CustomerZip { get; set; }

    /// <summary>Ort des Kunden.</summary>
    public string? CustomerCity { get; set; }

    /// <summary>Name des Auftraggebers.</summary>
    public string? Client { get; set; }

    /// <summary>Straße und Hausnummer des Auftraggebers.</summary>
    public string? ClientStreet { get; set; }

    /// <summary>Zweite Adresszeile des Auftraggebers.</summary>
    public string? ClientLine2 { get; set; }

    /// <summary>Postleitzahl des Auftraggebers.</summary>
    public string? ClientZip { get; set; }

    /// <summary>Ort des Auftraggebers.</summary>
    public string? ClientCity { get; set; }

    /// <summary>
    /// Name der ersten Ansprechperson. Wird beim Speichern aus <see cref="Contacts"/> gepflegt,
    /// damit Übersicht, Suche und ältere Vorlagen weiter funktionieren.
    /// </summary>
    public string? Contact { get; set; }

    /// <summary>Ansprechpersonen beim Kunden, in der eingegebenen Reihenfolge.</summary>
    public List<ProjectContact> Contacts { get; set; } = [];

    /// <summary>Ansprechpersonen beim Auftraggeber, in der eingegebenen Reihenfolge.</summary>
    public List<ProjectContact> ClientContacts { get; set; } = [];

    /// <summary>Meilensteine des Projekts, etwa die Begehung vor Ort.</summary>
    public List<ProjectMilestone> Milestones { get; set; } = [];

    /// <summary>Anschrift in einer Zeile, z. B. „Hauptstraße 12, 12345 Musterstadt“.</summary>
    public string CustomerAddress => ComposeAddress(CustomerStreet, CustomerLine2, CustomerZip, CustomerCity);

    /// <summary>Anschrift des Auftraggebers in einer Zeile.</summary>
    public string ClientAddress => ComposeAddress(ClientStreet, ClientLine2, ClientZip, ClientCity);

    /// <summary>Anschrift in Zeilen: Straße, Zusatz und „PLZ Ort“ untereinander.</summary>
    public IReadOnlyList<string> CustomerAddressLines =>
        ComposeAddressLines(CustomerStreet, CustomerLine2, CustomerZip, CustomerCity);

    /// <summary>Anschrift des Auftraggebers in Zeilen.</summary>
    public IReadOnlyList<string> ClientAddressLines =>
        ComposeAddressLines(ClientStreet, ClientLine2, ClientZip, ClientCity);

    /// <summary>Straße, zweite Zeile und „PLZ Ort“ durch Komma verbunden; Leeres entfällt.</summary>
    private static string ComposeAddress(string? street, string? line2, string? zip, string? city) =>
        string.Join(", ", ComposeAddressLines(street, line2, zip, city));

    /// <summary>Straße, zweite Zeile und „PLZ Ort“ als eigene Zeilen; Leeres entfällt.</summary>
    private static IReadOnlyList<string> ComposeAddressLines(string? street, string? line2, string? zip, string? city)
    {
        var cityLine = string.Join(" ", new[] { zip, city }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        return new[] { street, line2, cityLine }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!)
            .ToArray();
    }

    public string? Description { get; set; }

    /// <summary>Verantwortlicher Mitarbeiter.</summary>
    public Guid? ResponsibleUserId { get; set; }

    /// <summary>Name des Verantwortlichen zum Zeitpunkt der Zuweisung (denormalisiert).</summary>
    public string? ResponsibleName { get; set; }

    /// <summary>Absoluter Pfad des Projektordners, kann auf jedem Laufwerk liegen.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Bild für das Deckblatt. Beim Speichern wird die gewählte Datei in den Bilderordner des
    /// Projekts kopiert; hier steht danach der Pfad im Projektordner.
    /// </summary>
    public string? ImagePath { get; set; }

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
    public string? CustomerLine2 { get; set; }
    public string? CustomerZip { get; set; }
    public string? CustomerCity { get; set; }

    public string? Client { get; set; }
    public string? ClientStreet { get; set; }
    public string? ClientLine2 { get; set; }
    public string? ClientZip { get; set; }
    public string? ClientCity { get; set; }

    /// <summary>Ansprechpersonen beim Kunden; leere Zeilen werden beim Speichern ignoriert.</summary>
    public IReadOnlyList<ProjectContactInput> Contacts { get; set; } = [];

    /// <summary>Ansprechpersonen beim Auftraggeber; leere Zeilen werden beim Speichern ignoriert.</summary>
    public IReadOnlyList<ProjectContactInput> ClientContacts { get; set; } = [];

    /// <summary>Meilensteine; leere Zeilen werden beim Speichern ignoriert.</summary>
    public IReadOnlyList<ProjectMilestoneInput> Milestones { get; set; } = [];

    /// <summary>Name der ersten Ansprechperson beim Kunden, abgeleitet aus <see cref="Contacts"/>.</summary>
    public string? PrimaryContact => Contacts.Count > 0 ? Contacts[0].Name : null;

    public string? Description { get; set; }
    public Guid? ResponsibleUserId { get; set; }
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Gewähltes Bild für das Deckblatt; wird beim Speichern in den Projekttordner kopiert.</summary>
    public string? ImagePath { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    /// <summary>Ordner samt Unterordnern anlegen, wenn er noch nicht existiert.</summary>
    public bool CreateFolderIfMissing { get; set; } = true;
}

/// <summary>Zu wem eine Ansprechperson gehört: Kunde oder Auftraggeber.</summary>
public enum ProjectContactKind
{
    Customer = 0,
    Client = 1,
}

/// <summary>Ansprechperson beim Kunden oder beim Auftraggeber.</summary>
public sealed class ProjectContact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }

    /// <summary>Kunde oder Auftraggeber; bestimmt, in welcher Liste die Person erscheint.</summary>
    public ProjectContactKind Kind { get; set; } = ProjectContactKind.Customer;

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
    /// <summary>Kunde oder Auftraggeber.</summary>
    public ProjectContactKind Kind { get; set; } = ProjectContactKind.Customer;

    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Responsibility { get; set; }
}

/// <summary>Ein Meilenstein im Projekt, etwa die Begehung vor Ort.</summary>
public sealed class ProjectMilestone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Abgehakt: der Meilenstein ist erledigt.</summary>
    public bool IsDone { get; set; }

    /// <summary>Datum, an dem der Meilenstein erledigt wurde.</summary>
    public DateTime? DoneDate { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Anzeige für Listen: „erledigt am 30.07.2026“ oder „offen“.</summary>
    public string StatusText => IsDone
        ? DoneDate is { } date ? $"erledigt am {date:dd.MM.yyyy}" : "erledigt"
        : "offen";
}

/// <summary>Feste Meilensteine, die jedes Projekt mitbringt.</summary>
public static class ProjectMilestoneCatalog
{
    /// <summary>
    /// Die Begehung vor Ort. Ihr Titel ist fest und sie kann nicht entfernt werden;
    /// ihr Datum steht auf dem Deckblatt.
    /// </summary>
    public const string Inspection = "Begehung";
}

/// <summary>Eingabe eines Meilensteins aus dem Projektdialog.</summary>
public sealed class ProjectMilestoneInput
{
    public string Title { get; set; } = string.Empty;
    public bool IsDone { get; set; }
    public DateTime? DoneDate { get; set; }
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

    /// <summary>Projektbild; die Übersicht zeigt es beim Überfahren der Zeile.</summary>
    public string ImagePath { get; init; } = string.Empty;

    public ProjectStatus Status { get; init; }
    public string StatusText => ProjectStatusCatalog.Describe(Status);
    public bool IsArchived => Status == ProjectStatus.Archived;
    public string ArchivedText { get; init; } = string.Empty;
    public string UpdatedText { get; init; } = string.Empty;
    public string UpdatedBy { get; init; } = string.Empty;
    public string FolderState { get; init; } = string.Empty;
}
