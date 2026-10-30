namespace LiftUp.Core.Domain;

/// <summary>HTML-Vorlage für PDF-Dokumente. Jede Änderung erzeugt eine neue Version.</summary>
public sealed class DocumentTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string DocumentType { get; set; } = "Dokument";
    public int CurrentVersion { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }

    public long RowVersion { get; set; } = 1;
    public bool IsDeleted { get; set; }
}

/// <summary>Unveränderlicher Stand einer Vorlage.</summary>
public sealed class TemplateVersion
{
    public Guid Id { get; init; }
    public Guid TemplateId { get; init; }
    public int Version { get; init; }
    public string Html { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public string CreatedBy { get; init; } = string.Empty;

    public string VersionText => $"Version {Version}";
    public string CreatedText => CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
}

public sealed class TemplateListItem
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DocumentType { get; init; } = string.Empty;
    public int CurrentVersion { get; init; }
    public bool IsActive { get; init; }
    public string StatusText => IsActive ? "aktiv" : "inaktiv";
    public string VersionText => $"v{CurrentVersion}";
    public string UpdatedText { get; init; } = string.Empty;
}

public sealed class TemplateInput
{
    public string Name { get; set; } = string.Empty;
    public string DocumentType { get; set; } = "Dokument";
    public string Html { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

/// <summary>Ein erzeugtes PDF samt der verwendeten Vorlagenversion.</summary>
public sealed class ProjectDocument
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public Guid TemplateId { get; init; }
    public string TemplateName { get; init; } = string.Empty;
    public int TemplateVersion { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string CreatedBy { get; init; } = string.Empty;

    public string CreatedText => CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string VersionText => $"v{TemplateVersion}";
    public string SizeText => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : $"{SizeBytes / 1024.0:0.0} KB";
    public bool FileExists => File.Exists(FilePath);
}

/// <summary>Zentral verwalteter Textbaustein (HTML-Fragment).</summary>
public sealed class TextSnippet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string Html { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public int Version { get; set; } = 1;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }

    public long RowVersion { get; set; } = 1;
}

public sealed class TextSnippetListItem
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public int Version { get; init; }
    public bool IsArchived { get; init; }
    public string StatusText => IsArchived ? "archiviert" : "aktiv";
    public string VersionText => $"v{Version}";
    public string UpdatedText { get; init; } = string.Empty;
}

public sealed class SnippetInput
{
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string Html { get; set; } = string.Empty;
}
