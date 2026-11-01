using System.Text;

namespace LiftUp.Core.Domain;

/// <summary>Ein Unterpunkt der Analyse, z. B. „2.1 Aggregat“.</summary>
public sealed record ProjectAnalysisSection(string Key, string Number, string Title)
{
    /// <summary>Überschrift im Reiter und im Dokument, etwa „2.1 Aggregat“ – ohne Untertitel nur „2.1“.</summary>
    public string Heading => Title.Length == 0 ? Number : $"{Number} {Title}";
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
    /// <summary>
    /// Tabelle, die den Haupttitel füllt, etwa die Zustandsanalyse unter „1. Anlagedaten“;
    /// <c>null</c>, wenn der Haupttitel aus Texten besteht.
    /// </summary>
    public string? TableKey { get; init; }

    /// <summary>Überschrift im Reiter und im Dokument, etwa „2. Analyse“.</summary>
    public string Heading => $"{Number.TrimEnd('.')}. {Title}";
}

/// <summary>Erfasster Text eines Unterpunkts; der Inhalt ist HTML wie in den Vorlagen.</summary>
public sealed class ProjectAnalysisText
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }

    /// <summary>Schlüssel des Unterpunkts; er bleibt beim Umbenennen des Untertitels gleich.</summary>
    public string SectionKey { get; set; } = string.Empty;

    /// <summary>Untertitel des Punkts, etwa „Aggregat“; leer, solange keiner gesetzt ist.</summary>
    public string Title { get; set; } = string.Empty;

    public string Html { get; set; } = string.Empty;

    /// <summary>Reihenfolge im Reiter „2. Analyse“ und im Bericht.</summary>
    public int SortOrder { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
    public long RowVersion { get; set; } = 1;
}

/// <summary>Ein Unterpunkt, wie er aus der Oberfläche zum Speichern kommt.</summary>
public sealed record ProjectAnalysisTextInput(string SectionKey, string Title, string Html);

/// <summary>
/// Aufbau der Analyse im Zustandsbericht: ein Haupttitel („2. Analyse“) mit den
/// Unterpunkten der Anlagenbewertung. Die Punkte stehen nicht fest – sie entstehen aus
/// den erfassten Texten, damit je Projekt mal zwei, mal zehn Punkte entstehen.
/// </summary>
public static class ProjectAnalysisCatalog
{
    public const string AnalyseKey = "Analyse";
    public const string AnlagedatenKey = "Anlagedaten";
    public const string FazitKey = "Fazit";

    /// <summary>
    /// Haupttitel „1. Anlagedaten“: die Datenaufnahme der Aufzüge als Tabelle, wie sie
    /// unter „Zustandsanalyse“ gepflegt wird.
    /// </summary>
    public static ProjectAnalysisChapter Anlagedaten { get; } = new(
        AnlagedatenKey,
        "1",
        "Anlagedaten",
        [])
    {
        TableKey = ProjectTableCatalog.ZustandsanalyseKey,
    };

    /// <summary>
    /// Haupttitel „2. Analyse“. Seine Unterpunkte (2.1, 2.2 …) stehen nicht fest: Sie
    /// entstehen aus den erfassten Texten, siehe <see cref="SectionsFor"/>.
    /// </summary>
    public static ProjectAnalysisChapter Analyse { get; } = new(
        AnalyseKey,
        "2",
        "Analyse",
        []);

    /// <summary>Haupttitel „3. Fazit / Empfehlung“ mit dem abschließenden Text.</summary>
    public static ProjectAnalysisChapter Fazit { get; } = new(
        FazitKey,
        "3",
        "Fazit / Empfehlung",
        [new ProjectAnalysisSection(FazitKey, "3.1", "Fazit und Empfehlung")]);

    /// <summary>Alle Haupttitel in der Reihenfolge des Berichts.</summary>
    public static IReadOnlyList<ProjectAnalysisChapter> All { get; } = [Anlagedaten, Analyse, Fazit];

    public static ProjectAnalysisChapter? FindChapter(string? key) =>
        All.FirstOrDefault(chapter => string.Equals(chapter.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Ob ein gespeicherter Text zum Fazit gehört statt zu „2. Analyse“.</summary>
    public static bool IsFazit(string? key) =>
        string.Equals(key?.Trim(), FazitKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Die Analysepunkte eines Projekts in gespeicherter Reihenfolge: „2.1“, „2.2“ …
    /// Ein Punkt ohne Untertitel heißt nur „2.2“.
    /// </summary>
    public static IReadOnlyList<ProjectAnalysisSection> SectionsFor(IReadOnlyList<ProjectAnalysisText>? texts)
    {
        var sections = new List<ProjectAnalysisSection>();
        foreach (var text in (texts ?? []).OrderBy(text => text.SortOrder))
        {
            if (string.IsNullOrWhiteSpace(text.SectionKey) || IsFazit(text.SectionKey))
            {
                continue;
            }

            sections.Add(new ProjectAnalysisSection(
                text.SectionKey,
                $"2.{sections.Count + 1}",
                (text.Title ?? string.Empty).Trim()));
        }

        return sections;
    }

    /// <summary>Findet einen Unterpunkt über Schlüssel, Nummer („2.1“) oder Untertitel.</summary>
    public static ProjectAnalysisSection? FindSection(IReadOnlyList<ProjectAnalysisText>? texts, string? keyNumberOrTitle)
    {
        var value = keyNumberOrTitle?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        foreach (var section in SectionsFor(texts).Append(Fazit.Sections[0]))
        {
            if (section.Key.Equals(value, StringComparison.OrdinalIgnoreCase)
                || section.Number.Equals(value, StringComparison.OrdinalIgnoreCase)
                || section.Title.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return section;
            }
        }

        return null;
    }

    /// <summary>
    /// Erkennt den Haupttitel in einem Vorlagenabschnitt – „2. Analyse“ ebenso wie „Analyse“.
    /// Damit kann der Editor den Reiter mit den erfassten Texten vorbelegen.
    /// </summary>
    public static bool IsChapterTitle(string? title) => IsTitle(title, Analyse);

    /// <summary>
    /// Erkennt den Haupttitel „1. Anlagedaten“ in einem Vorlagenabschnitt – „1. Anlagedaten“
    /// ebenso wie „Anlagedaten“. Beim Erzeugen des Dokuments stehen dort die erfassten
    /// Anlagedaten anstelle der leeren Tabelle der Vorlage.
    /// </summary>
    public static bool IsAnlagedatenTitle(string? title) => IsTitle(title, Anlagedaten);

    private static bool IsTitle(string? title, ProjectAnalysisChapter chapter)
    {
        var value = StripNumber(title);
        return value.Length > 0 && value.Equals(chapter.Title, StringComparison.OrdinalIgnoreCase);
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
        if (!string.IsNullOrWhiteSpace(section))
        {
            var single = FindSection(texts, section);
            return single is not null && Filled(texts).TryGetValue(single.Key, out var html)
                ? SectionHtml(single, html, HasHeading(single)) + SpacingStyle
                : string.Empty;
        }

        return string.Concat(RenderChapters(texts, includeChapterTitle));
    }

    /// <summary>
    /// Die erfassten Kapitel einzeln, in der Reihenfolge des Berichts – damit jedes Kapitel
    /// auf eine eigene Seite kommt. Leere Kapitel fehlen.
    /// </summary>
    public static IReadOnlyList<string> RenderChapters(
        IReadOnlyList<ProjectAnalysisText>? texts,
        bool includeChapterTitle = true)
    {
        var filled = Filled(texts);
        var chapters = new List<string>();

        // „2. Analyse“: die Punkte 2.1 … 2.n in der Reihenfolge des Reiters.
        var written = SectionsFor(texts)
            .Where(section => filled.ContainsKey(section.Key))
            .ToArray();
        if (written.Length > 0)
        {
            var builder = new StringBuilder();
            if (includeChapterTitle)
            {
                builder.Append(ChapterTitle(Analyse));
            }

            foreach (var section in written)
            {
                builder.Append(SectionHtml(section, filled[section.Key], HasHeading(section)));
            }

            chapters.Add(builder.ToString());
        }

        // „3. Fazit / Empfehlung“: der eine Punkt steht ohne Zwischenüberschrift darunter.
        var fazit = Fazit.Sections[0];
        if (filled.TryGetValue(fazit.Key, out var fazitHtml))
        {
            var builder = new StringBuilder();
            if (includeChapterTitle)
            {
                builder.Append(ChapterTitle(Fazit));
            }

            builder.Append(SectionHtml(fazit, fazitHtml, withHeading: false));
            chapters.Add(builder.ToString());
        }

        // Der Abstand zwischen Untertitel und Text ist überall gleich; die Regel steht einmal
        // im Dokument, auch wenn mehrere Kapitel folgen. Sie steht am Ende, damit die Kapitel
        // mit ihrer Überschrift beginnen.
        if (chapters.Count > 0)
        {
            chapters[^1] += SpacingStyle;
        }

        return chapters;
    }

    /// <summary>Die erfassten Texte nach Schlüssel; leere Texte fehlen.</summary>
    private static Dictionary<string, string> Filled(IReadOnlyList<ProjectAnalysisText>? texts)
    {
        var filled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(text.Html) && !string.IsNullOrWhiteSpace(text.SectionKey))
            {
                filled[text.SectionKey] = text.Html;
            }
        }

        return filled;
    }

    private static string ChapterTitle(ProjectAnalysisChapter chapter) =>
        "<h2 style=\"font-size:15pt;margin:0 0 4mm 0;\">" + Encode(chapter.Heading) + "</h2>";

    /// <summary>
    /// Der Abstand zwischen Untertitel und Text kommt allein vom Untertitel: Der obere Rand des
    /// ersten Blocks im Text entfällt. So sieht er bei jedem Text gleich aus.
    /// </summary>
    private const string SpacingStyle =
        "<style>.liftup-analysis>:first-child{margin-top:0 !important;}</style>";

    private static readonly System.Text.RegularExpressions.Regex TrailingBreaks = new(
        @"(?:<br\s*/?>)+$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex TrailingEmptyBlock = new(
        @"<(?<tag>p|div)(?:\s[^>]*)?>(?:\s|&nbsp;|&#160;|<br\s*/?>)*</\k<tag>>$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Entfernt Zeilenumbrüche und leere Absätze am Ende eines Textes: Aus „test“ mit ein paar
    /// Leerzeilen darunter wird „test“, aus „test“ mit einem leeren Absatz nur „test“.
    /// </summary>
    public static string TrimTrailing(string? html)
    {
        var value = (html ?? string.Empty).Trim();

        while (value.Length > 0)
        {
            var shorter = TrailingEmptyBlock
                .Replace(TrailingBreaks.Replace(value, string.Empty).TrimEnd(), string.Empty)
                .TrimEnd();

            if (shorter == value)
            {
                break;
            }

            value = shorter;
        }

        return value;
    }

    private static string SectionHtml(ProjectAnalysisSection section, string html, bool withHeading) =>
        (withHeading
            ? "<h3 style=\"font-size:12pt;margin:4mm 0 1.5mm 0;break-after:avoid;page-break-after:avoid;\">"
              + Encode(section.Heading)
              + "</h3>"
            : string.Empty)
        + "<div class=\"liftup-analysis\">"
        + html
        + "</div>";

    /// <summary>
    /// Ob ein Unterpunkt eine eigene Zwischenüberschrift trägt: Ein Punkt ohne Untertitel
    /// steht ohne Überschrift im Bericht, das Fazit ebenfalls (sein Haupttitel genügt).
    /// </summary>
    private static bool HasHeading(ProjectAnalysisSection section) =>
        !IsFazit(section.Key) && section.Title.Length > 0;

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
