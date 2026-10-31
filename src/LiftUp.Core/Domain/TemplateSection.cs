namespace LiftUp.Core.Domain;

/// <summary>
/// Ein Abschnitt (Untertitel) einer Vorlage, z. B. „Einleitung“ oder „Fazit“.
/// Die Reihenfolge bestimmt die Reihenfolge im Dokument.
/// </summary>
public sealed class TemplateSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Startinhalt des Tabs (HTML); leer heißt: der Tab beginnt leer.</summary>
    public string DefaultHtml { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}

/// <summary>Ausgefüllter Abschnitt eines Dokuments: Untertitel und Inhalt als HTML.</summary>
public sealed record DocumentSectionContent(string Title, string Html);
