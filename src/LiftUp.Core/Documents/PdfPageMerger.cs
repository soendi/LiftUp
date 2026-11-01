using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace LiftUp.Core.Documents;

/// <summary>
/// Führt mehrere PDF-Dateien zu einem Dokument zusammen. Damit lassen sich Seiten mit
/// unterschiedlicher Ausrichtung in einem PDF unterbringen: Der Bericht behält seine
/// Einstellung, die Tabellenblätter kommen im Querformat dazu.
/// </summary>
public static class PdfPageMerger
{
    /// <summary>
    /// Hängt die Seiten aller Teile in der übergebenen Reihenfolge aneinander.
    /// <c>null</c> bedeutet, dass die Teile nicht gelesen werden konnten – der Aufrufer
    /// druckt dann wie bisher alles in einem Zug.
    /// </summary>
    public static byte[]? TryMerge(IReadOnlyList<byte[]> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Count <= 1)
        {
            return parts.Count == 1 ? parts[0] : null;
        }

        try
        {
            using var target = new PdfDocument();
            foreach (var part in parts)
            {
                using var source = PdfReader.Open(
                    new MemoryStream(part, writable: false), PdfDocumentOpenMode.Import);

                for (var index = 0; index < source.PageCount; index++)
                {
                    target.AddPage(source.Pages[index]);
                }
            }

            using var output = new MemoryStream();
            target.Save(output, closeStream: false);
            return output.ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ein nicht lesbares Teil darf das Erzeugen nicht verhindern; die Blätter werden
            // dann vom Aufrufer gemeinsam mit dem Bericht gedruckt.
            return null;
        }
    }
}
