namespace LiftUp.Core.Domain;

/// <summary>Bild in der zentralen Bibliothek oder einem Projekt zugeordnet.</summary>
public sealed class LibraryImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Description { get; set; }

    /// <summary>Schlagworte, durch Komma getrennt – für die Suche mit „&amp;bild“.</summary>
    public string? Tags { get; set; }

    /// <summary>Null bedeutet: globale Bibliothek, sonst projektbezogen.</summary>
    public Guid? ProjectId { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 des Dateiinhalts; erkennt umbenannte Bilder wieder.</summary>
    public string? ContentHash { get; set; }

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
    public string? Tags { get; set; }
    public Guid? ProjectId { get; set; }
}

public sealed class ImageListItem
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public bool IsArchived { get; init; }
    public bool IsGlobal { get; init; }

    /// <summary>Falsch, wenn die Datei nicht mehr am gespeicherten Pfad liegt.</summary>
    public bool FileExists { get; init; } = true;

    public string ScopeText => IsGlobal ? "global" : "Projekt";
    public string StatusText => IsArchived ? "archiviert" : "aktiv";

    /// <summary>Einzelne Schlagworte für die Klickliste in der Bilderansicht.</summary>
    public IReadOnlyList<string> TagList => ImageTags.Split(Tags);
    public string FileStatusText => FileExists ? "vorhanden" : "fehlt";
    public string Placeholder => $"{{{{bild:{Name}}}}}";
    public string CreatedText { get; init; } = string.Empty;
}
