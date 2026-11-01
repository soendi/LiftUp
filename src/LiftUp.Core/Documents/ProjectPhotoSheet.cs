using System.Globalization;
using System.Text;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Documents;

/// <summary>
/// Stellt die Fotos der Analyse als eigene Seiten zusammen: je Aufzug eine Überschrift und
/// darunter ein Raster mit zwei Fotos pro Zeile. Markierungen (Kreis, Pfeil) liegen als SVG
/// über dem Bild und sitzen dadurch an derselben Stelle wie im Editor.
/// </summary>
public static class ProjectPhotoSheet
{
    /// <summary>Höhe einer Bildzelle im Raster.</summary>
    private const double CellHeightMm = 72;

    /// <summary>HTML der Fotoseiten; leer, wenn es keine Fotos gibt.</summary>
    /// <param name="pageBreak">Beim Anhängen an das Dokument <c>true</c>, als eigenes Blatt <c>false</c>.</param>
    /// <param name="lifts">
    /// Aufzüge der Zustandsanalyse in ihrer Reihenfolge. Ist die Liste gesetzt, erscheinen nur
    /// Fotos zu diesen Aufzügen – Fotos zu einem gelöschten oder umbenannten Aufzug bleiben weg.
    /// </param>
    public static string Render(
        IReadOnlyList<ProjectAnalysisPhoto> photos,
        bool pageBreak = true,
        IReadOnlyList<ProjectAnalysisLift>? lifts = null)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count == 0)
        {
            return string.Empty;
        }

        // Die Nummer folgt den Fotoseiten: Aufzüge ohne Fotos zählen nicht mit, damit nach einem
        // gelöschten Aufzug keine Lücke bleibt („4.1“ statt „4.2“).
        var groups = lifts is null
            ? photos
                .GroupBy(photo => photo.LiftKey, StringComparer.OrdinalIgnoreCase)
                .Select((group, number) => (
                    Number: number,
                    Name: group.Key,
                    Location: string.Empty,
                    Photos: (IReadOnlyList<ProjectAnalysisPhoto>)group.ToArray()))
                .ToArray()
            : lifts
                .Select(lift => (
                    Lift: lift,
                    Photos: (IReadOnlyList<ProjectAnalysisPhoto>)photos
                        .Where(photo => string.Equals(photo.LiftKey, lift.Key, StringComparison.OrdinalIgnoreCase))
                        .ToArray()))
                .Where(entry => entry.Photos.Count > 0)
                .Select((entry, number) => (
                    Number: number,
                    Name: entry.Lift.Name,
                    Location: entry.Lift.Location,
                    Photos: entry.Photos))
                .ToArray();

        if (groups.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append(pageBreak ? "<div style=\"page-break-before: always;\">" : "<div>");
        builder.Append("<h2 style=\"font-size:15pt;margin:0 0 4mm 0;\">")
            .Append(Encode(ProjectAnalysisPhotoCatalog.Heading))
            .Append("</h2>");

        foreach (var group in groups)
        {
            builder.Append("<h3 style=\"font-size:12pt;margin:4mm 0 2mm 0;break-after:avoid;page-break-after:avoid;\">")
                .Append(Encode(ProjectAnalysisPhotoCatalog.LiftHeading(group.Number, group.Name, group.Location)))
                .Append("</h3>");

            builder.Append("<div style=\"display:grid;grid-template-columns:1fr 1fr;gap:4mm;\">");
            foreach (var photo in group.Photos)
            {
                AppendPhoto(builder, photo);
            }

            builder.Append("</div>");
        }

        builder.Append("</div>");
        return builder.ToString();
    }

    /// <summary>Die Fotoseiten als eigenes Dokument; wird getrennt gedruckt und angehängt.</summary>
    public static string RenderDocument(
        IReadOnlyList<ProjectAnalysisPhoto> photos,
        PdfPageSettings? settings = null,
        IReadOnlyList<ProjectAnalysisLift>? lifts = null)
    {
        var body = Render(photos, pageBreak: false, lifts);
        if (body.Length == 0)
        {
            return string.Empty;
        }

        var margin = (settings?.MarginMm is { } value ? Math.Max(value, 0) : 15)
            .ToString("0.##", CultureInfo.InvariantCulture);
        var format = string.Equals(settings?.Format, "A3", StringComparison.OrdinalIgnoreCase) ? "A3" : "A4";
        var size = settings?.Landscape == true ? format + " landscape" : format;

        return "<!DOCTYPE html>\n<html lang=\"de\">\n<head>\n  <meta charset=\"utf-8\" />\n  <style>\n"
            + $"    @page {{ size: {size}; margin: {margin}mm; }}\n"
            + "    body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 9pt; color: #1f2933; margin: 0; }\n"
            + "    figure { margin: 0; break-inside: avoid; page-break-inside: avoid; }\n"
            + "    figcaption { font-size: 8pt; color: #486581; margin-top: 1.5mm; }\n"
            + "  </style>\n</head>\n<body>"
            + body
            + "</body>\n</html>";
    }

    /// <summary>Eine Bildzelle: feste Höhe, Bild mittig, Markierungen darüber, Bildunterschrift darunter.</summary>
    private static void AppendPhoto(StringBuilder builder, ProjectAnalysisPhoto photo)
    {
        var image = File.Exists(photo.FilePath)
            ? ImageEmbedding.ToImageTag(photo.FilePath, photo.DisplayText)
            : null;

        builder.Append("<figure>");
        builder.Append("<div style=\"height:")
            .Append(CellHeightMm.ToString("0.##", CultureInfo.InvariantCulture))
            .Append("mm;display:flex;align-items:center;justify-content:center;background:#ffffff;")
            .Append("border:1px solid #d9e2ec;border-radius:2mm;overflow:hidden;\">");

        if (image is null)
        {
            builder.Append("<span style=\"font-size:8pt;color:#9aa5b1;\">Bild nicht gefunden</span>");
        }
        else
        {
            builder.Append("<div style=\"position:relative;display:inline-block;line-height:0;\">");
            builder.Append(image.Replace(
                "style=\"max-width:100%;\"",
                "style=\"display:block;max-height:" + (CellHeightMm - 4).ToString("0.##", CultureInfo.InvariantCulture)
                + "mm;max-width:100%;width:auto;height:auto;\"",
                StringComparison.Ordinal));
            builder.Append(Overlay(photo));
            builder.Append("</div>");
        }

        builder.Append("</div>");

        if (!string.IsNullOrWhiteSpace(photo.Caption))
        {
            builder.Append("<figcaption>").Append(Encode(photo.Caption)).Append("</figcaption>");
        }

        builder.Append("</figure>");
    }

    /// <summary>Markierungen als SVG in Bildpixeln – deckungsgleich mit dem Bild darüber.</summary>
    private static string Overlay(ProjectAnalysisPhoto photo)
    {
        if (photo.Annotations.Count == 0 || photo.ImageWidth <= 0 || photo.ImageHeight <= 0)
        {
            return string.Empty;
        }

        var width = photo.ImageWidth.ToString("0.##", CultureInfo.InvariantCulture);
        var height = photo.ImageHeight.ToString("0.##", CultureInfo.InvariantCulture);
        var stroke = ProjectAnalysisPhotoCatalog.StrokeWidth(photo.ImageWidth, photo.ImageHeight)
            .ToString("0.##", CultureInfo.InvariantCulture);

        var builder = new StringBuilder();
        builder.Append("<svg viewBox=\"0 0 ").Append(width).Append(' ').Append(height)
            .Append("\" style=\"position:absolute;left:0;top:0;width:100%;height:100%;pointer-events:none;\">");

        foreach (var annotation in photo.Annotations)
        {
            if (annotation.Kind == PhotoAnnotationKind.Circle)
            {
                builder.Append("<circle cx=\"").Append(N(annotation.X1))
                    .Append("\" cy=\"").Append(N(annotation.Y1))
                    .Append("\" r=\"").Append(N(annotation.Radius))
                    .Append("\" fill=\"none\" stroke=\"#e11d48\" stroke-width=\"").Append(stroke).Append("\" />");
                continue;
            }

            builder.Append("<line x1=\"").Append(N(annotation.X1))
                .Append("\" y1=\"").Append(N(annotation.Y1))
                .Append("\" x2=\"").Append(N(annotation.X2))
                .Append("\" y2=\"").Append(N(annotation.Y2))
                .Append("\" stroke=\"#e11d48\" stroke-width=\"").Append(stroke)
                .Append("\" stroke-linecap=\"round\" />");

            var head = ProjectAnalysisPhotoCatalog.ArrowHead(
                annotation, ProjectAnalysisPhotoCatalog.StrokeWidth(photo.ImageWidth, photo.ImageHeight) * 5);
            builder.Append("<polygon points=\"");
            builder.AppendJoin(' ', head.Select(point => N(point.X) + "," + N(point.Y)));
            builder.Append("\" fill=\"#e11d48\" />");
        }

        builder.Append("</svg>");
        return builder.ToString();
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Encode(string? value) => (value ?? string.Empty)
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
