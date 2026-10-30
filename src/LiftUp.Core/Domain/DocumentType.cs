namespace LiftUp.Core.Domain;

/// <summary>
/// Dokumenttyp (Angebot, Rechnung, Bericht …). Vorlagen werden einem Typ zugeordnet,
/// damit der PDF-Reiter nur die passenden Vorlagen anbietet.
/// </summary>
public sealed class DocumentType
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public bool IsArchived { get; init; }

    public string StatusText => IsArchived ? "archiviert" : "aktiv";

    public override string ToString() => Name;
}

public sealed class DocumentTypeInput
{
    public string Name { get; set; } = string.Empty;
}
