using System.Text;

namespace LiftUp.Core.Domain;

/// <summary>Ein Unterpunkt der Analyse, z. B. „2.1 Aggregat“.</summary>
public sealed record ProjectAnalysisSection(string Key, string Number, string Title)
{
    /// <summary>Überschrift im Reiter und im Dokument, etwa „2.1 Aggregat“.</summary>
    public string Heading => $"{Number} {Title}";
}

/// <summary>
/// Ein Haupttitel der Analyse samt seinen Unterpunkten, z. B. „2. Analyse“ mit
/// „2.1 Aggregat“ und „2.2 Hubzylinder / Tragmittel“.
/// </summary>
public sealed record ProjectAnalysisChapter(
    string Key,
    string Number,
    string Title,
    IReadOnlyList<ProjectAnalysisSection> Sections)
{
    /// <summary>Überschrift im Reiter und im Dokument, etwa „2. Analyse“.</summary>
    public string Heading => $"{Number.TrimEnd('.')}. {Title}";
}

/// <summary>Erfasster Text eines Unterpunkts; der Inhalt ist HTML wie in den Vorlagen.</summary>
public sealed class ProjectAnalysisText
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }

    /// <summary>Schlüssel des Unterpunkts aus <see cref="ProjectAnalysisCatalog"/>.</summary>
    public string SectionKey { get; set; } = string.Empty;

    public string Html { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
    public long RowVersion { get; set; } = 1;
}

/// <summary>Ein Unterpunkt, wie er aus der Oberfläche zum Speichern kommt.</summary>
public sealed record ProjectAnalysisTextInput(string SectionKey, string Html);

/// <summary>
/// Aufbau der Analyse im Zustandsbericht: ein Haupttitel („2. Analyse“) mit den
/// Unterpunkten der Anlagenbewertung. Die Nummern stehen hier, damit Reiter und
/// Dokument dieselbe Gliederung zeigen.
/// </summary>
public static class ProjectAnalysisCatalog
{
    public const string AnalyseKey = "Analyse";

    /// <summary>Haupttitel „2. Analyse“ mit den Unterpunkten aus der Datenaufnahme.</summary>
    public static ProjectAnalysisChapter Analyse { get; } = new(
        AnalyseKey,
        "2",
        "Analyse",
        [
            new("Aggregat", "2.1", "Aggregat"),
            new("Tragmittel", "2.2", "Hubzylinder / Tragmittel"),
            new("Steuerung", "2.3", "Steuerung / Regler"),
            new("Regulator", "2.4", "Regulator (Geschwindigkeitsbegrenzer)"),
            new("Fangvorrichtung", "2.5", "Fangvorrichtung"),
            new("Kabine", "2.6", "Kabine / Triebmechanik"),
            new("Zugänglichkeit", "2.7", "Zugänglichkeit"),
        ]);

    /// <summary>Alle Haupttitel in der Reihenfolge des Berichts.</summary>
    public static IReadOnlyList<ProjectAnalysisChapter> All { get; } = [Analyse];

    /// <summary>Alle Unterpunkte in der Reihenfolge des Berichts.</summary>
    public static IReadOnlyList<ProjectAnalysisSection> Sections => Analyse.Sections;

    public static ProjectAnalysisChapter? FindChapter(string? key) =>
        All.FirstOrDefault(chapter => string.Equals(chapter.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Findet einen Unterpunkt über Schlüssel, Nummer („2.1“) oder Titel.</summary>
    public static ProjectAnalysisSection? FindSection(string? keyNumberOrTitle)
    {
        var value = keyNumberOrTitle?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return Sections.FirstOrDefault(section =>
            section.Key.Equals(value, StringComparison.OrdinalIgnoreCase)
            || section.Number.Equals(value, StringComparison.OrdinalIgnoreCase)
            || section.Title.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Erkennt den Haupttitel in einem Vorlagenabschnitt – „2. Analyse“ ebenso wie „Analyse“.
    /// Damit kann der Editor den Reiter mit den erfassten Texten vorbelegen.
    /// </summary>
    public static bool IsChapterTitle(string? title)
    {
        var value = StripNumber(title);
        return value.Length > 0 && value.Equals(Analyse.Title, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// HTML der Analyse: ohne <paramref name="section"/> alle erfassten Unterpunkte unter
    /// dem Haupttitel, sonst nur der eine Unterpunkt. Leere Unterpunkte fallen weg.
    /// <paramref name="includeChapterTitle"/> entfällt, wenn der Haupttitel schon als
    /// Überschrift im Dokument steht (etwa im Reiter „2. Analyse“).
    /// </summary>
    public static string Render(
        IReadOnlyList<ProjectAnalysisText>? texts,
        string? section = null,
        bool includeChapterTitle = true)
    {
        var filled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(text.Html))
            {
                filled[text.SectionKey] = text.Html;
            }
        }

        if (!string.IsNullOrWhiteSpace(section))
        {
            var single = FindSection(section);
            return single is not null && filled.TryGetValue(single.Key, out var html)
                ? SectionHtml(single, html)
                : string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var chapter in All)
        {
            var written = chapter.Sections
                .Where(item => filled.ContainsKey(item.Key))
                .ToArray();
            if (written.Length == 0)
            {
                continue;
            }

            if (includeChapterTitle)
            {
                builder.Append("<h2 style=\"font-size:15pt;margin:0 0 4mm 0;\">")
                    .Append(Encode(chapter.Heading))
                    .Append("</h2>");
            }

            foreach (var item in written)
            {
                builder.Append(SectionHtml(item, filled[item.Key]));
            }
        }

        return builder.ToString();
    }

    private static string SectionHtml(ProjectAnalysisSection section, string html) =>
        "<h3 style=\"font-size:12pt;margin:4mm 0 1.5mm 0;break-after:avoid;page-break-after:avoid;\">"
        + Encode(section.Heading)
        + "</h3><div class=\"liftup-analysis\">"
        + html
        + "</div>";

    /// <summary>Entfernt eine führende Nummer samt Punkt, etwa „2. “ aus „2. Analyse“.</summary>
    private static string StripNumber(string? title)
    {
        var value = (title ?? string.Empty).Trim();
        while (value.Length > 0 && (char.IsDigit(value[0]) || value[0] == '.'))
        {
            value = value[1..];
        }

        return value.Trim();
    }

    private static string Encode(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
