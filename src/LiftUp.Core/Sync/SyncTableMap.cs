namespace LiftUp.Core.Sync;

/// <summary>
/// Welche Bereiche des Änderungsprotokolls abgeglichen werden. Übertragen werden
/// ganze Datensätze (nicht die kurzen Protokollnotizen), damit der andere
/// Arbeitsplatz den Stand vollständig übernehmen kann.
///
/// Ausgenommen bleiben Dokumente und Bilder: dort steht in der Datenbank nur ein
/// Dateipfad, die Datei selbst liegt im Projektordner des jeweiligen Rechners.
/// Ein übertragener Pfad würde auf dem anderen Arbeitsplatz ins Leere zeigen.
/// </summary>
public static class SyncTableMap
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Project"] = "Projects",
        ["User"] = "Users",
        ["Role"] = "Roles",
        ["Template"] = "Templates",
        ["Snippet"] = "TextSnippets",
        ["DocumentType"] = "DocumentTypes",
    };

    /// <summary>Bereiche, die tatsächlich übertragen werden.</summary>
    public static IReadOnlyList<string> EntityTypes { get; } = [.. Tables.Keys];

    public static bool TryGetTable(string entityType, out string table)
    {
        if (Tables.TryGetValue(entityType, out var found))
        {
            table = found;
            return true;
        }

        table = string.Empty;
        return false;
    }

    /// <summary>Erklärung, warum ein Bereich nicht übertragen wird.</summary>
    public static string SkipReason(string entityType) => entityType switch
    {
        "Document" => "Erzeugte Dokumente bleiben am Arbeitsplatz, weil die PDF-Datei dort liegt.",
        "Image" => "Bilder bleiben am Arbeitsplatz, weil die Bilddatei dort liegt.",
        _ => $"Für den Bereich „{entityType}“ ist kein Abgleich vorgesehen.",
    };
}
