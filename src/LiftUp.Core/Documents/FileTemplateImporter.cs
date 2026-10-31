using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Documents;

/// <summary>Ergebnis eines Dateiimports für den Vorlageneditor.</summary>
public sealed class ImportedTemplate
{
    public required string SuggestedName { get; init; }
    public required string Html { get; init; }
    public string SourceKind { get; init; } = string.Empty;
}

/// <summary>
/// Wandelt vorhandene Dateien in eine HTML-Vorlage um: Excel-Arbeitsmappen und
/// CSV werden als Tabelle übernommen, Word-Absätze samt Tabellen, HTML-Dateien
/// unverändert und Textdateien als Absätze. So lassen sich bestehende Dokumente
/// als Ausgangspunkt für eine Vorlage verwenden.
/// </summary>
public static class FileTemplateImporter
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static IReadOnlyList<string> SupportedExtensions { get; } = [".xlsx", ".csv", ".html", ".htm", ".txt", ".docx"];

    public static ImportedTemplate Import(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new ValidationException("Die ausgewählte Datei wurde nicht gefunden.");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(path);

        return extension switch
        {
            ".xlsx" => new ImportedTemplate
            {
                SuggestedName = name,
                Html = WrapDocument(XlsxToHtml(path)),
                SourceKind = "Excel",
            },
            ".csv" => new ImportedTemplate
            {
                SuggestedName = name,
                Html = WrapDocument(CsvToHtml(path)),
                SourceKind = "CSV",
            },
            ".html" or ".htm" => new ImportedTemplate
            {
                SuggestedName = name,
                Html = ReadHtml(path),
                SourceKind = "HTML",
            },
            ".txt" => new ImportedTemplate
            {
                SuggestedName = name,
                Html = WrapDocument(TextToHtml(path)),
                SourceKind = "Text",
            },
            ".docx" => new ImportedTemplate
            {
                SuggestedName = name,
                Html = WrapDocument(DocxToHtml(path)),
                SourceKind = "Word",
            },
            _ => throw new ValidationException(
                $"Der Dateityp „{extension}“ wird nicht unterstützt. Möglich sind: {string.Join(", ", SupportedExtensions)}."),
        };
    }

    private static string WrapDocument(string body) => $$"""
        <!DOCTYPE html>
        <html lang="de">
        <head>
        <meta charset="utf-8" />
        <style>
          @page { size: A4; margin: 18mm; }
          body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 10.5pt; color: #1f2933; }
          h1 { font-size: 16pt; margin: 0 0 4mm 0; }
          h2 { font-size: 13pt; margin: 6mm 0 3mm 0; }
          h3 { font-size: 11.5pt; margin: 4mm 0 2mm 0; }
          p { margin: 0 0 3mm 0; }
          table { width: 100%; border-collapse: collapse; margin: 0 0 6mm 0; }
          td, th { border: 1px solid #b8c0cc; padding: 4px 6px; vertical-align: top; font-size: 9.5pt; }
          th { background: #eef2f7; text-align: left; }
        </style>
        </head>
        <body>
        {{body}}
        </body>
        </html>
        """;

    private static string ReadHtml(string path)
    {
        var text = File.ReadAllText(path, DetectEncoding(path));
        return text.Contains("<html", StringComparison.OrdinalIgnoreCase)
            ? text
            : WrapDocument(text);
    }

    private static string TextToHtml(string path)
    {
        var text = File.ReadAllText(path, DetectEncoding(path));
        var builder = new StringBuilder();

        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            builder.Append(string.IsNullOrWhiteSpace(paragraph)
                ? "<p>&nbsp;</p>"
                : "<p>" + Escape(paragraph) + "</p>");
        }

        return builder.ToString();
    }

    private static string CsvToHtml(string path)
    {
        var text = File.ReadAllText(path, DetectEncoding(path));
        var rows = ParseDelimited(text, DetectSeparator(text));
        return RenderTable(rows);
    }

    private static char DetectSeparator(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var candidates = new[] { ';', ',', '\t' };
        return candidates
            .OrderByDescending(candidate => firstLine.Count(character => character == candidate))
            .First();
    }

    private static List<List<string>> ParseDelimited(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (character == '"')
            {
                inQuotes = true;
                hasContent = true;
            }
            else if (character == separator)
            {
                row.Add(field.ToString());
                field.Clear();
                hasContent = true;
            }
            else if (character == '\r')
            {
                // Zeilenende wird über \n behandelt.
            }
            else if (character == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
                hasContent = false;
            }
            else
            {
                field.Append(character);
                hasContent = true;
            }
        }

        if (hasContent || field.Length > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static string XlsxToHtml(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        var sharedStrings = ReadSharedStrings(archive);
        var dateStyles = ReadDateStyles(archive);
        var builder = new StringBuilder();
        var sheets = FindSheets(archive);

        if (sheets.Count == 0)
        {
            throw new ValidationException("In der Excel-Datei wurden keine Tabellenblätter gefunden.");
        }

        foreach (var (name, entryPath) in sheets)
        {
            var sheet = LoadXml(archive, entryPath);
            if (sheet?.Root is null)
            {
                continue;
            }

            var rows = ReadRows(sheet, sharedStrings, dateStyles);
            if (rows.Count == 0)
            {
                continue;
            }

            builder.Append("<h2>").Append(Escape(name)).Append("</h2>");
            builder.Append(RenderTable(rows));
        }

        return builder.Length == 0
            ? "<p>Die Arbeitsmappe enthält keine Inhalte.</p>"
            : builder.ToString();
    }

    private static List<(string Name, string EntryPath)> FindSheets(ZipArchive archive)
    {
        var workbook = LoadXml(archive, "xl/workbook.xml");
        var relationships = ReadWorkbookRelationships(archive);
        var sheets = new List<(string, string)>();

        var elements = workbook?.Root?
            .Element(Spreadsheet + "sheets")?
            .Elements(Spreadsheet + "sheet")
            .ToList() ?? [];

        var fallbackIndex = 1;
        foreach (var sheet in elements)
        {
            var name = (string?)sheet.Attribute("name") ?? $"Tabelle {fallbackIndex}";
            var relationshipId = (string?)sheet.Attribute(OfficeRelationships + "id");

            var entryPath = relationshipId is not null && relationships.TryGetValue(relationshipId, out var target)
                ? "xl/" + target.TrimStart('/').Replace("xl/", string.Empty, StringComparison.OrdinalIgnoreCase)
                : $"xl/worksheets/sheet{fallbackIndex}.xml";

            if (archive.GetEntry(entryPath) is not null)
            {
                sheets.Add((name, entryPath));
            }

            fallbackIndex++;
        }

        if (sheets.Count > 0)
        {
            return sheets;
        }

        foreach (var entry in archive.Entries
                     .Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
        {
            sheets.Add(($"Tabelle {sheets.Count + 1}", entry.FullName));
        }

        return sheets;
    }

    private static Dictionary<string, string> ReadWorkbookRelationships(ZipArchive archive)
    {
        var relationships = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = LoadXml(archive, "xl/_rels/workbook.xml.rels");
        if (document?.Root is null)
        {
            return relationships;
        }

        foreach (var relationship in document.Root.Elements())
        {
            var id = (string?)relationship.Attribute("Id");
            var target = (string?)relationship.Attribute("Target");
            if (id is not null && target is not null)
            {
                relationships[id] = target;
            }
        }

        return relationships;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var document = LoadXml(archive, "xl/sharedStrings.xml");
        var strings = new List<string>();
        if (document?.Root is null)
        {
            return strings;
        }

        foreach (var item in document.Root.Elements(Spreadsheet + "si"))
        {
            strings.Add(string.Concat(item.Descendants(Spreadsheet + "t").Select(text => text.Value)));
        }

        return strings;
    }

    /// <summary>Ermittelt, welche Zellformatierungen Datum oder Uhrzeit darstellen.</summary>
    private static HashSet<int> ReadDateStyles(ZipArchive archive)
    {
        var styles = new HashSet<int>();
        var document = LoadXml(archive, "xl/styles.xml");
        if (document?.Root is null)
        {
            return styles;
        }

        var customFormats = new Dictionary<int, string>();
        foreach (var format in document.Root.Elements(Spreadsheet + "numFmts").Elements(Spreadsheet + "numFmt"))
        {
            if (int.TryParse((string?)format.Attribute("numFmtId"), out var id))
            {
                customFormats[id] = (string?)format.Attribute("formatCode") ?? string.Empty;
            }
        }

        var index = 0;
        foreach (var cellFormat in document.Root.Elements(Spreadsheet + "cellXfs").Elements(Spreadsheet + "xf"))
        {
            if (int.TryParse((string?)cellFormat.Attribute("numFmtId"), out var formatId) && IsDateFormat(formatId, customFormats))
            {
                styles.Add(index);
            }

            index++;
        }

        return styles;
    }

    private static bool IsDateFormat(int formatId, Dictionary<int, string> customFormats)
    {
        if (formatId is >= 14 and <= 22 or >= 45 and <= 47)
        {
            return true;
        }

        if (!customFormats.TryGetValue(formatId, out var code))
        {
            return false;
        }

        var cleaned = code.Replace("\"", string.Empty);
        return cleaned.Contains('y', StringComparison.OrdinalIgnoreCase) ||
               cleaned.Contains('d', StringComparison.OrdinalIgnoreCase);
    }

    private static List<List<string>> ReadRows(XDocument sheet, List<string> sharedStrings, HashSet<int> dateStyles)
    {
        var rows = new List<List<string>>();
        var sheetData = sheet.Root?.Element(Spreadsheet + "sheetData");
        if (sheetData is null)
        {
            return rows;
        }

        foreach (var row in sheetData.Elements(Spreadsheet + "row"))
        {
            var cells = new List<string>();

            foreach (var cell in row.Elements(Spreadsheet + "c"))
            {
                var column = ColumnIndex((string?)cell.Attribute("r"));
                while (cells.Count < column)
                {
                    cells.Add(string.Empty);
                }

                cells.Add(CellText(cell, sharedStrings, dateStyles));
            }

            while (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[^1]))
            {
                cells.RemoveAt(cells.Count - 1);
            }

            if (cells.Count > 0)
            {
                rows.Add(cells);
            }
        }

        return rows;
    }

    private static string CellText(XElement cell, List<string> sharedStrings, HashSet<int> dateStyles)
    {
        var type = (string?)cell.Attribute("t");

        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(cell.Descendants(Spreadsheet + "t").Select(text => text.Value));
        }

        var value = cell.Element(Spreadsheet + "v")?.Value;
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        switch (type)
        {
            case "s":
                return int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count
                    ? sharedStrings[sharedIndex]
                    : string.Empty;
            case "b":
                return value == "1" ? "wahr" : "falsch";
            case "str":
            case "e":
                return value;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return value;
        }

        var style = int.TryParse((string?)cell.Attribute("s"), out var styleIndex) ? styleIndex : -1;
        if (style >= 0 && dateStyles.Contains(style) && number is > 0 and < 2958466)
        {
            return DateTime.FromOADate(number).ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
        }

        return number.ToString("0.##########", CultureInfo.CurrentCulture);
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return 0;
        }

        var column = 0;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            column = (column * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return Math.Max(column - 1, 0);
    }

    private static string DocxToHtml(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var document = LoadXml(archive, "word/document.xml")
                       ?? throw new ValidationException("In der Word-Datei wurde kein Text gefunden.");

        var body = document.Root?.Element(Word + "body");
        if (body is null)
        {
            throw new ValidationException("In der Word-Datei wurde kein Text gefunden.");
        }

        var builder = new StringBuilder();

        foreach (var element in body.Elements())
        {
            if (element.Name == Word + "p")
            {
                AppendParagraph(builder, element);
            }
            else if (element.Name == Word + "tbl")
            {
                AppendWordTable(builder, element);
            }
        }

        return builder.Length == 0 ? "<p>Das Word-Dokument enthält keine Absätze.</p>" : builder.ToString();
    }

    private static void AppendParagraph(StringBuilder builder, XElement paragraph)
    {
        var style = (string?)paragraph
            .Element(Word + "pPr")?
            .Element(Word + "pStyle")?
            .Attribute(Word + "val");

        var content = new StringBuilder();
        AppendRuns(content, paragraph);

        if (content.Length == 0)
        {
            builder.Append("<p>&nbsp;</p>");
            return;
        }

        var tag = style?.StartsWith("Heading1", StringComparison.OrdinalIgnoreCase) == true
            ? "h2"
            : style?.StartsWith("Heading2", StringComparison.OrdinalIgnoreCase) == true
                ? "h3"
                : "p";

        builder.Append('<').Append(tag).Append('>')
            .Append(content)
            .Append("</").Append(tag).Append('>');
    }

    private static void AppendRuns(StringBuilder builder, XElement paragraph)
    {
        foreach (var run in paragraph.Elements(Word + "r"))
        {
            var text = string.Concat(run.Elements(Word + "t").Select(element => element.Value));
            if (text.Length == 0)
            {
                continue;
            }

            var properties = run.Element(Word + "rPr");
            var formatted = Escape(text);

            if (properties?.Element(Word + "b") is not null)
            {
                formatted = "<strong>" + formatted + "</strong>";
            }

            if (properties?.Element(Word + "i") is not null)
            {
                formatted = "<em>" + formatted + "</em>";
            }

            if (properties?.Element(Word + "u") is not null)
            {
                formatted = "<u>" + formatted + "</u>";
            }

            builder.Append(formatted);
        }
    }

    private static void AppendWordTable(StringBuilder builder, XElement table)
    {
        builder.Append("<table>");
        foreach (var row in table.Elements(Word + "tr"))
        {
            builder.Append("<tr>");
            foreach (var cell in row.Elements(Word + "tc"))
            {
                builder.Append("<td>");
                var paragraphs = cell.Elements(Word + "p").ToList();
                for (var index = 0; index < paragraphs.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append("<br />");
                    }

                    AppendRuns(builder, paragraphs[index]);
                }

                builder.Append("</td>");
            }

            builder.Append("</tr>");
        }

        builder.Append("</table>");
    }

    private static string RenderTable(List<List<string>> rows)
    {
        if (rows.Count == 0)
        {
            return "<p>Keine Tabelleninhalte gefunden.</p>";
        }

        var columns = rows.Max(row => row.Count);
        var builder = new StringBuilder("<table>");

        foreach (var row in rows)
        {
            builder.Append("<tr>");
            for (var index = 0; index < columns; index++)
            {
                builder.Append("<td>")
                    .Append(Escape(index < row.Count ? row[index] : string.Empty))
                    .Append("</td>");
            }

            builder.Append("</tr>");
        }

        return builder.Append("</table>").ToString();
    }

    private static XDocument? LoadXml(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null)
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static Encoding DetectEncoding(string path)
    {
        using var stream = File.OpenRead(path);
        var bom = new byte[4];
        var read = stream.Read(bom, 0, bom.Length);

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        return Encoding.UTF8;
    }

    private static string Escape(string? value) => (value ?? string.Empty)
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
