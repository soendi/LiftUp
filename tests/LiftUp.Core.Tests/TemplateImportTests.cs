using System.IO.Compression;
using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class TemplateImportTests
{
    [Fact]
    public void Excel_arbeitsmappe_wird_zur_tabelle()
    {
        using var fixture = new TestDatabase();
        var path = Path.Combine(fixture.Root, "Angebot.xlsx");
        CreateXlsx(path);

        var imported = FileTemplateImporter.Import(path);

        Assert.Equal("Angebot", imported.SuggestedName);
        Assert.Equal("Excel", imported.SourceKind);
        Assert.Contains("<table>", imported.Html);
        Assert.Contains("Tabelle1", imported.Html);
        Assert.Contains("Material", imported.Html);
        Assert.Contains("<td>42</td>", imported.Html);
    }

    [Fact]
    public void Csv_mit_semikolon_und_anfuehrungszeichen_wird_zur_tabelle()
    {
        using var fixture = new TestDatabase();
        var path = Path.Combine(fixture.Root, "liste.csv");
        File.WriteAllText(path, "Name;Betrag\n\"Muster, AG\";120.50\n", Encoding.UTF8);

        var imported = FileTemplateImporter.Import(path);

        Assert.Contains("Name", imported.Html);
        Assert.Contains("Muster, AG", imported.Html);
        Assert.Contains("120.50", imported.Html);
        Assert.Contains("<td>", imported.Html);
    }

    [Fact]
    public void Textdatei_wird_als_absaetze_uebernommen()
    {
        using var fixture = new TestDatabase();
        var path = Path.Combine(fixture.Root, "notiz.txt");
        File.WriteAllText(path, "Guten Tag\n\nMit <Sonderzeichen>", Encoding.UTF8);

        var imported = FileTemplateImporter.Import(path);

        Assert.Contains("<p>Guten Tag</p>", imported.Html);
        Assert.Contains("&lt;Sonderzeichen&gt;", imported.Html);
        Assert.DoesNotContain("<Sonderzeichen>", imported.Html);
    }

    [Fact]
    public void Html_datei_bleibt_unveraendert()
    {
        using var fixture = new TestDatabase();
        var path = Path.Combine(fixture.Root, "seite.html");
        File.WriteAllText(path, "<html><body><h1>Bericht</h1></body></html>", Encoding.UTF8);

        var imported = FileTemplateImporter.Import(path);

        Assert.Equal("HTML", imported.SourceKind);
        Assert.Contains("<h1>Bericht</h1>", imported.Html);
    }

    [Fact]
    public void Word_absaetze_und_formatierung_werden_uebernommen()
    {
        using var fixture = new TestDatabase();
        var path = Path.Combine(fixture.Root, "bericht.docx");
        CreateDocx(path);

        var imported = FileTemplateImporter.Import(path);

        Assert.Equal("Word", imported.SourceKind);
        Assert.Contains("<p>Einleitung</p>", imported.Html);
        Assert.Contains("<strong>wichtig</strong>", imported.Html);
        Assert.Contains("<table>", imported.Html);
        Assert.Contains("<td>Wert</td>", imported.Html);
    }

    [Fact]
    public void Unbekannter_dateityp_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var path = Path.Combine(fixture.Root, "bild.png");
        File.WriteAllBytes(path, [1, 2, 3]);

        var exception = Assert.Throws<ValidationException>(() => FileTemplateImporter.Import(path));
        Assert.Contains("nicht unterstützt", exception.Message);
    }

    private static void CreateXlsx(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="Tabelle1" sheetId="1" r:id="rId1" />
              </sheets>
            </workbook>
            """);

        WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml" />
            </Relationships>
            """);

        WriteEntry(archive, "xl/sharedStrings.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="2" uniqueCount="2">
              <si><t>Material</t></si>
              <si><t>Schrauben</t></si>
            </sst>
            """);

        WriteEntry(archive, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <cellXfs count="1"><xf numFmtId="0" /></cellXfs>
            </styleSheet>
            """);

        WriteEntry(archive, "xl/worksheets/sheet1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1">
                  <c r="A1" t="s"><v>0</v></c>
                  <c r="B1" t="s"><v>1</v></c>
                </row>
                <row r="2">
                  <c r="A2"><v>42</v></c>
                </row>
              </sheetData>
            </worksheet>
            """);
    }

    private static void CreateDocx(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteEntry(archive, "word/document.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>Einleitung</w:t></w:r></w:p>
                <w:p>
                  <w:r><w:t>Das ist </w:t></w:r>
                  <w:r><w:rPr><w:b /></w:rPr><w:t>wichtig</w:t></w:r>
                </w:p>
                <w:tbl>
                  <w:tr>
                    <w:tc><w:p><w:r><w:t>Wert</w:t></w:r></w:p></w:tc>
                  </w:tr>
                </w:tbl>
              </w:body>
            </w:document>
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}
