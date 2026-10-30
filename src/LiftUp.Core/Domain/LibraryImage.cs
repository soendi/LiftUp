namespace LiftUp.Core.Domain;

/// <summary>Bild in der zentralen Bibliothek oder einem Projekt zugeordnet.</summary>
public sealed class LibraryImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Description { get; set; }

    /// <summary>Null bedeutet: globale Bibliothek, sonst projektbezogen.</summary>
    public Guid? ProjectId { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public bool IsArchived { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public long RowVersion { get; set; } = 1;

    public string SizeText => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : $"{SizeBytes / 1024.0:0.0} KB";
}

public sealed class ImageInput
{
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Description { get; set; }
    public Guid? ProjectId { get; set; }
}

public sealed class ImageListItem
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public bool IsArchived { get; init; }
    public bool IsGlobal { get; init; }
    public string ScopeText => IsGlobal ? "global" : "Projekt";
    public string StatusText => IsArchived ? "archiviert" : "aktiv";
    public string Placeholder => $"{{{{bild:{Name}}}}}";
    public string CreatedText { get; init; } = string.Empty;
}
