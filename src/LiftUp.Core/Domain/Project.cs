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

    public static string Describe(ProjectStatus status) =>
        All.FirstOrDefault(info => info.Value == status)?.Name ?? status.ToString();
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Projektnummer, optional, muss eindeutig sein.</summary>
    public string? Number { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Customer { get; set; }
    public string? Description { get; set; }

    /// <summary>Absoluter Pfad des Projektordners, kann auf jedem Laufwerk liegen.</summary>
    public string FolderPath { get; set; } = string.Empty;

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }

    public long RowVersion { get; set; } = 1;
    public bool IsDeleted { get; set; }
}

/// <summary>Eingabedaten aus dem Projektdialog.</summary>
public sealed class ProjectInput
{
    public string Name { get; set; } = string.Empty;
    public string? Number { get; set; }
    public string? Customer { get; set; }
    public string? Description { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    /// <summary>Ordner anlegen, wenn er noch nicht existiert.</summary>
    public bool CreateFolderIfMissing { get; set; } = true;
}

public sealed class ProjectListItem
{
    public Guid Id { get; init; }
    public string Number { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Customer { get; init; } = string.Empty;
    public string FolderPath { get; init; } = string.Empty;
    public ProjectStatus Status { get; init; }
    public string StatusText => ProjectStatusCatalog.Describe(Status);
    public string UpdatedText { get; init; } = string.Empty;
    public string UpdatedBy { get; init; } = string.Empty;
    public string FolderState { get; init; } = string.Empty;
}
