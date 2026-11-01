namespace LiftUp.Core.Domain;

/// <summary>Art einer Spalte; bestimmt die Ausrichtung und die Farben in der PDF-Tabelle.</summary>
public enum ProjectTableColumnKind
{
    Text,
    Number,
    Date,

    /// <summary>Bewertung wie im Excel (grün / gelb / rot) – die Farbe ergibt sich aus dem Wert.</summary>
    Rating,
}

/// <summary>Eine Spalte einer projektbezogenen Tabelle.</summary>
public sealed record ProjectTableColumn(string Key, string Label, ProjectTableColumnKind Kind, double Width)
{
    /// <summary>Feste Auswahl in der Tabelle; leer, wenn frei getippt wird.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>Gruppenkopf im PDF (etwa „Anlagendaten“); leer heißt ohne Gruppe.</summary>
    public string Group { get; init; } = string.Empty;
}

/// <summary>
/// Zeile der Legende: Titel und Erläuterung, etwa „Bewertung“ und „grün = gut, gelb = mässig“.
/// </summary>
public sealed record ProjectTableLegend(string Title, string Details);

/// <summary>
/// Beschreibung einer projektbezogenen Tabelle. Die Spalten stehen bewusst nur hier:
/// Ein Datenbankfeld je Spalte wäre bei jeder Vorlagenänderung eine Migration.
/// </summary>
public sealed record ProjectTableDefinition(string Key, string Title, IReadOnlyList<ProjectTableColumn> Columns)
{
    /// <summary>Hinweistext unter dem Titel (steht so in der Excel-Vorlage).</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Gegliederte Legende unter dem Titel – je Eintrag eine Zeile.</summary>
    public IReadOnlyList<ProjectTableLegend> Legend { get; init; } = [];
}

/// <summary>
/// Die beiden Tabellen aus den Excel-Vorlagen „Aufzüge.xlsx“ und „Investition.xlsx“.
/// Die Reihenfolge entspricht der Reihenfolge der Blätter im erzeugten PDF.
/// </summary>
public static class ProjectTableCatalog
{
    public const string ZustandsanalyseKey = "Zustandsanalyse";
    public const string InvestitionsplanungKey = "Investitionsplanung";

    private const string Anlagendaten = "Anlagendaten";
    private const string Analysepunkte = "Analysepunkte";
    private const string Esba = "ESBA";
    /// <summary>Gekürzt, damit der Gruppenkopf im PDF die Spalten nicht breit macht.</summary>
    private const string Ersatzteile = "Ersatzteilverfgbk.";
    private const string VarianteModernisierung = "Variante 1 – Modernisierung";
    private const string VarianteErsatz = "Variante 2 – Ersatzanlage";

    /// <summary>Bewertung der Analysepunkte (Legende der Vorlage: grün gut, gelb mässig, rot schlecht).</summary>
    private static readonly string[] AnalyseWerte = ["gut", "mässig", "schlecht"];

    /// <summary>ESBA: grün in Ordnung, rot nicht in Ordnung.</summary>
    private static readonly string[] EsbaWerte = ["in Ordnung", "nicht in Ordnung"];

    /// <summary>Ersatzteile: grün verfügbar, gelb bedingt verfügbar, rot nicht verfügbar.</summary>
    private static readonly string[] ErsatzteilWerte = ["verfügbar", "bedingt verfügbar", "nicht verfügbar"];

    /// <summary>Kurzzeichen der Aufzugsart gemäss Legende der Vorlage.</summary>
    private static readonly string[] Aufzugsarten = ["PA", "WA", "SA", "KA", "BA", "FT"];

    private static readonly string[] JaNein = ["ja", "nein"];

    /// <summary>Blatt „Datenaufnahme der Aufzüge“ aus „Aufzüge.xlsx“.</summary>
    public static ProjectTableDefinition Zustandsanalyse { get; } = new(
        ZustandsanalyseKey,
        "Datenaufnahme der Aufzüge",
        [
            Column("AssetName", "Anlagebezeichnung", ProjectTableColumnKind.Text, 120, Anlagendaten),
            Column("Service", "Servicefirma", ProjectTableColumnKind.Text, 120, Anlagendaten),
            Column("Location", "Standort", ProjectTableColumnKind.Text, 150, Anlagendaten),
            Column("Type", "Art des Aufzugs", ProjectTableColumnKind.Text, 110, Anlagendaten, Aufzugsarten),
            Column("Year", "Baujahr", ProjectTableColumnKind.Number, 70, Anlagendaten),
            Column("Kind", "Anlageart", ProjectTableColumnKind.Text, 110, Anlagendaten),
            Column("Load", "Nutzlast (kg)", ProjectTableColumnKind.Number, 80, Anlagendaten),
            Column("Floors", "Anzahl Stockwerke", ProjectTableColumnKind.Number, 90, Anlagendaten),
            Column("Trips", "Anzahl Züge", ProjectTableColumnKind.Number, 80, Anlagendaten),
            Column("EmergencyCall", "Notrufsystem nach EN81-28 (ja/nein)", ProjectTableColumnKind.Text, 130, Anlagendaten, JaNein),
            Column("EmergencyTechnology", "Technologie Notrufsystem", ProjectTableColumnKind.Text, 120, Anlagendaten, ["GSM", "ANALOG"]),
            Column("AutomaticDoors", "automatische Türen (ja/nein)", ProjectTableColumnKind.Text, 120, Anlagendaten, JaNein),
            Column("Maintenance", "Anzahl Wartungen pro Jahr", ProjectTableColumnKind.Number, 100, Anlagendaten),

            Column("Drive", "Aggregat / Antrieb", ProjectTableColumnKind.Rating, 110, Analysepunkte, AnalyseWerte),
            Column("Traction", "Treibscheibe / Treibmittel", ProjectTableColumnKind.Rating, 120, Analysepunkte, AnalyseWerte),
            Column("Control", "Steuerung / Regler", ProjectTableColumnKind.Rating, 110, Analysepunkte, AnalyseWerte),
            Column("Governor", "Regulator (Geschwindigkeitsbegrenzer)", ProjectTableColumnKind.Rating, 140, Analysepunkte, AnalyseWerte),
            Column("Safety", "Fangvorrichtung", ProjectTableColumnKind.Rating, 110, Analysepunkte, AnalyseWerte),
            Column("Cabin", "Kabine / Triebmechanik", ProjectTableColumnKind.Rating, 120, Analysepunkte, AnalyseWerte),
            Column("Accessibility", "Zugänglichkeit", ProjectTableColumnKind.Rating, 110, Analysepunkte, AnalyseWerte),

            Column("EsbaCondition", "Anlagengegebenheit", ProjectTableColumnKind.Rating, 110, Esba, EsbaWerte),
            Column("EsbaDoors", "Kabinenabschluss", ProjectTableColumnKind.Rating, 120, Esba, EsbaWerte),
            Column("EsbaEmergency", "Notrufsystem / Notbeleuchtung", ProjectTableColumnKind.Rating, 140, Esba, EsbaWerte),
            Column("EsbaBuffer", "Puffer", ProjectTableColumnKind.Rating, 80, Esba, EsbaWerte),
            Column("EsbaArea", "Verhältnis von Nutzfläche zur Nennlast", ProjectTableColumnKind.Rating, 150, Esba, EsbaWerte),
            Column("EsbaGlass", "Ungeeignetes Glas in Schachttüren", ProjectTableColumnKind.Rating, 140, Esba, EsbaWerte),

            Column("SpareControl", "Steuerung", ProjectTableColumnKind.Rating, 110, Ersatzteile, ErsatzteilWerte),
            Column("SpareDrive", "Antrieb / Aggregat", ProjectTableColumnKind.Rating, 120, Ersatzteile, ErsatzteilWerte),
            Column("SpareDoors", "Türantrieb", ProjectTableColumnKind.Rating, 110, Ersatzteile, ErsatzteilWerte),
            Column("SpareGovernor", "Regulator (Geschwindigkeitsbegrenzer)", ProjectTableColumnKind.Rating, 140, Ersatzteile, ErsatzteilWerte),
        ])
    {
        Legend =
        [
            new("Bewertung", "grün = gut, gelb = mässig, rot = schlecht."),
            new("ESBA (Erhöhung der Sicherheit an bestehenden Aufzügen)", "grün = in Ordnung, rot = nicht in Ordnung."),
            new("Ersatzteilverfügbarkeit", "grün = verfügbar, gelb = bedingt verfügbar, rot = nicht verfügbar."),
            new("Art des Aufzugs", "PA = Personenanlage, WA = Warenanlage, SA = Serviceanlage, KA = Kehrichtanlage, "
                + "BA = Bettenanlage, FT = Fahrtreppe."),
            new("Notruftechnologie", "GSM = über Funknetz, ANALOG = analoge Telefonleitung."),
        ],
    };

    /// <summary>Blatt „Investitionsplanung“ aus „Investition.xlsx“.</summary>
    public static ProjectTableDefinition Investitionsplanung { get; } = new(
        InvestitionsplanungKey,
        "Investitionsplanung",
        [
            Column("Year", "Jahr", ProjectTableColumnKind.Number, 60, Anlagendaten),
            Column("AssetName", "Anlagebezeichnung", ProjectTableColumnKind.Text, 120, Anlagendaten),
            Column("Service", "Servicefirma", ProjectTableColumnKind.Text, 120, Anlagendaten),
            Column("Location", "Standort", ProjectTableColumnKind.Text, 150, Anlagendaten),
            Column("Type", "Art des Aufzugs", ProjectTableColumnKind.Text, 100, Anlagendaten, Aufzugsarten),

            Column("ModernMeasure", "Massnahme", ProjectTableColumnKind.Text, 160, VarianteModernisierung),
            Column("ModernCost", "Kosten", ProjectTableColumnKind.Number, 90, VarianteModernisierung),
            Column("ModernNote", "Erläuterung", ProjectTableColumnKind.Text, 260, VarianteModernisierung),

            Column("ReplaceMeasure", "Massnahme", ProjectTableColumnKind.Text, 160, VarianteErsatz),
            Column("ReplaceCost", "Kosten", ProjectTableColumnKind.Number, 90, VarianteErsatz),
            Column("ReplaceNote", "Erläuterung", ProjectTableColumnKind.Text, 260, VarianteErsatz),
        ])
    {
        Note = "Investitionsplanung (Empfehlung). Bei den aufgeführten Massnahmen handelt es sich um "
            + "sicherheitsrelevante oder technologische Massnahmen. Massnahmen bezüglich Verschleiss "
            + "von Komponenten sind nicht aufgeführt.",
    };

    /// <summary>
    /// Spalten, die in beiden Tabellen vorkommen. Eine Zeile der Zustandsanalyse legt die
    /// passende Zeile der Investitionsplanung an und füllt diese Angaben von dort.
    /// </summary>
    public static IReadOnlyList<string> SharedWithInvestitionsplanung { get; } =
        ["Year", "AssetName", "Service", "Location", "Type"];

    /// <summary>Alle Tabellen in der Reihenfolge, in der sie im PDF erscheinen.</summary>
    public static IReadOnlyList<ProjectTableDefinition> All { get; } = [Zustandsanalyse, Investitionsplanung];

    public static ProjectTableDefinition? Find(string key) =>
        All.FirstOrDefault(definition => string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase));

    private static ProjectTableColumn Column(
        string key,
        string label,
        ProjectTableColumnKind kind,
        double width,
        string group,
        params string[] options) => new(key, label, kind, width)
        {
            Group = group,
            Options = options,
        };
}

/// <summary>Eine gespeicherte Zeile. Die Werte hängen an den Schlüsseln der Spalten.</summary>
public sealed class ProjectTableRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string TableKey { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long RowVersion { get; set; }
}

/// <summary>Eine Zeile, wie sie aus der Oberfläche zum Speichern kommt.</summary>
public sealed record ProjectTableRowInput(IReadOnlyDictionary<string, string> Values);
