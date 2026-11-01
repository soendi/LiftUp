namespace LiftUp.Core.Domain;

/// <summary>Art der Markierung auf einem Foto.</summary>
public enum PhotoAnnotationKind
{
    /// <summary>Kreis um eine Stelle, z. B. ein verschlissenes Bauteil.</summary>
    Circle = 0,

    /// <summary>Pfeil auf eine Stelle.</summary>
    Arrow = 1,
}

/// <summary>
/// Markierung auf einem Foto. Die Werte sind Bildpixel, damit Kreis und Pfeil im
/// Editor und im PDF an derselben Stelle sitzen – unabhängig von der Anzeigegröße.
/// </summary>
public sealed class PhotoAnnotation
{
    public PhotoAnnotationKind Kind { get; set; }

    /// <summary>Mittelpunkt des Kreises bzw. Anfang des Pfeils.</summary>
    public double X1 { get; set; }
    public double Y1 { get; set; }

    /// <summary>Pfeilspitze; beim Kreis nicht verwendet.</summary>
    public double X2 { get; set; }
    public double Y2 { get; set; }

    /// <summary>Radius des Kreises in Bildpixeln.</summary>
    public double Radius { get; set; }
}

/// <summary>Ein Aufzug aus der Zustandsanalyse: Anlagebezeichnung und Standort.</summary>
public sealed record ProjectAnalysisLift(string Name, string Location)
{
    /// <summary>Schlüssel der Fotos; die Anlagebezeichnung bleibt auch nach einer Umbenennung stabil.</summary>
    public string Key => Name;
}

/// <summary>Ein Foto der Analyse, das zu einem Aufzug gehört.</summary>
public sealed class ProjectAnalysisPhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }

    /// <summary>Anlagebezeichnung des Aufzugs aus der Zustandsanalyse; leer heißt „ohne Nummer“.</summary>
    public string LiftKey { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;

    /// <summary>Markierungen als Bildpixel; leer, wenn das Foto unverändert bleibt.</summary>
    public List<PhotoAnnotation> Annotations { get; set; } = [];

    /// <summary>Größe des Bildes in Pixeln; Grundlage für die Markierungen.</summary>
    public double ImageWidth { get; set; }
    public double ImageHeight { get; set; }

    public int SortOrder { get; set; }
    public long RowVersion { get; set; } = 1;

    /// <summary>Anzeige in Listen: Bildunterschrift oder Dateiname.</summary>
    public string DisplayText =>
        !string.IsNullOrWhiteSpace(Caption)
            ? Caption
            : Path.GetFileNameWithoutExtension(FilePath);
}

/// <summary>Ein Foto, wie es aus der Oberfläche zum Speichern kommt.</summary>
public sealed record ProjectAnalysisPhotoInput(
    string FilePath,
    string Caption,
    IReadOnlyList<PhotoAnnotation> Annotations,
    double ImageWidth,
    double ImageHeight);

/// <summary>
/// Der Foto-Teil der Analyse: Haupttitel „4. Fotos“ mit einem Unterpunkt je Aufzug aus
/// der Zustandsanalyse. Die Fotos erscheinen als eigene Seiten im PDF.
/// </summary>
public static class ProjectAnalysisPhotoCatalog
{
    public const string PhotosKey = "Fotos";
    public const string PhotosNumber = "4";
    public const string PhotosTitle = "Fotos";

    public static string Heading => $"{PhotosNumber}. {PhotosTitle}";

    /// <summary>
    /// Überschrift eines Aufzugs: Nummer, Anlagebezeichnung und Standort aus der
    /// Zustandsanalyse, etwa <c>4.1 8609861 Messligfeldstrasse 1</c>.
    /// </summary>
    public static string LiftHeading(int index, string? name, string? location = null)
    {
        var number = $"{PhotosNumber}.{index + 1}";
        var text = LiftText(name, location);

        return text.Length == 0 ? $"{number} Aufzug" : $"{number} {text}";
    }

    /// <summary>
    /// Überschrift ohne Nummer – für einen Foto-Reiter, der noch keine Fotos hat. Er erscheint
    /// nicht als Fotoseite im PDF und zählt deshalb bei der Nummerierung nicht mit.
    /// </summary>
    public static string LiftHeading(string? name, string? location = null)
    {
        var text = LiftText(name, location);
        return text.Length == 0 ? "Aufzug" : text;
    }

    /// <summary>Anlagebezeichnung und Standort als Text, ohne doppelte Leerzeichen.</summary>
    private static string LiftText(string? name, string? location) =>
        string.Join(" ", new[] { name?.Trim(), location?.Trim() }
            .Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>
    /// Die drei Punkte der Pfeilspitze: Spitze und die beiden Flanken. Der Winkel
    /// bleibt fest, die Länge hängt von der Bildgröße ab.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> ArrowHead(PhotoAnnotation annotation, double length)
    {
        var angle = Math.Atan2(annotation.Y2 - annotation.Y1, annotation.X2 - annotation.X1);
        const double spread = Math.PI / 7;

        return
        [
            (annotation.X2, annotation.Y2),
            (annotation.X2 - length * Math.Cos(angle - spread), annotation.Y2 - length * Math.Sin(angle - spread)),
            (annotation.X2 - length * Math.Cos(angle + spread), annotation.Y2 - length * Math.Sin(angle + spread)),
        ];
    }

    /// <summary>Strichstärke passend zur Bildgröße, damit Markierungen überall gleich wirken.</summary>
    public static double StrokeWidth(double imageWidth, double imageHeight)
    {
        var smallest = Math.Min(imageWidth, imageHeight);
        return smallest > 0 ? Math.Max(2, smallest * 0.008) : 4;
    }
}
