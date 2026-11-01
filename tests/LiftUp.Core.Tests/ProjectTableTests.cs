using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Zustandsanalyse und Investitionsplanung: Speichern, Lesen und das zweite Blatt.</summary>
public sealed class ProjectTableTests
{
    private sealed class FakeRenderer : IHtmlToPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(Encoding.UTF8.GetBytes("%PDF-1.4 " + html.Length));
    }

    /// <summary>Liefert echte, leere PDF-Seiten in der gewünschten Ausrichtung.</summary>
    private sealed class PageRenderer : IHtmlToPdfRenderer
    {
        public List<PdfPageSettings> Calls { get; } = [];

        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default)
        {
            Calls.Add(settings);

            using var document = new PdfDocument();
            var page = document.AddPage();
            page.Width = XUnit.FromMillimeter(settings.Landscape ? 297 : 210);
            page.Height = XUnit.FromMillimeter(settings.Landscape ? 210 : 297);

            using var stream = new MemoryStream();
            document.Save(stream, closeStream: false);
            return Task.FromResult(stream.ToArray());
        }
    }

    private static ProjectInput Input(string folder) => new()
    {
        Name = "Messligfeld 1 und 3, Sennewald",
        FolderPath = folder,
        Status = ProjectStatus.Active,
        CreateFolderIfMissing = true,
    };

    private static ProjectTableRowInput Row(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(pair => pair.Key, pair => pair.Value));

    [Fact]
    public void Zustandsanalyse_wird_gespeichert_und_gelesen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));

        var saved = tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
        [
            Row(
                ("AssetName", "8609861"),
                ("Location", "Messligfeldstrasse 1"),
                ("Type", "PA"),
                ("Year", "1994"),
                ("Drive", "schlecht")),
            Row(),
        ]);

        // Eine leere Zeile wird nicht gespeichert.
        Assert.Single(saved);

        var rows = tables.Get(project.Id, ProjectTableCatalog.ZustandsanalyseKey);
        Assert.Single(rows);
        Assert.Equal("8609861", rows[0].Values["AssetName"]);
        Assert.Equal("Messligfeldstrasse 1", rows[0].Values["Location"]);
        Assert.Equal("PA", rows[0].Values["Type"]);
        Assert.Equal("1994", rows[0].Values["Year"]);
        Assert.Equal("schlecht", rows[0].Values["Drive"]);
    }

    [Fact]
    public void Tabellen_sind_getrennt_und_werden_ersetzt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));

        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey, [Row(("AssetName", "8609861"))]);
        tables.Save(project.Id, ProjectTableCatalog.InvestitionsplanungKey, [Row(("ModernMeasure", "Nachhaltige Modernisierung"))]);
        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey, [Row(("AssetName", "8609862"))]);

        var analysis = tables.Get(project.Id, ProjectTableCatalog.ZustandsanalyseKey);
        var investment = tables.Get(project.Id, ProjectTableCatalog.InvestitionsplanungKey);

        Assert.Equal("8609862", Assert.Single(analysis).Values["AssetName"]);
        Assert.Equal("Nachhaltige Modernisierung", Assert.Single(investment).Values["ModernMeasure"]);
    }

    [Fact]
    public void Zustandsanalyse_wird_zweites_blatt_im_dokument()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new FakeRenderer());

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var template = templates.Create(new TemplateInput
        {
            Name = "Gutachten",
            Html = "<h1>{{project.name}}</h1>",
        });

        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
        [
            Row(("AssetName", "8609861"), ("Location", "Messligfeldstrasse 1"), ("Drive", "schlecht")),
        ]);

        var html = documents.RenderHtml(project.Id, template.Id);

        Assert.Contains("page-break-before: always", html);
        Assert.Contains("Datenaufnahme der Aufzüge", html);
        // Gruppenkopf und Spaltenkopf der Vorlage.
        Assert.Contains("Analysepunkte", html);
        Assert.Contains("Aggregat / Antrieb", html);
        Assert.Contains("8609861", html);
        Assert.Contains("Messligfeldstrasse 1", html);
        // „schlecht“ wird wie in der Vorlage rot hinterlegt.
        Assert.Contains("#ff9999", html);
        // Legende gegliedert, Notruftechnologie mit der richtigen Abkürzung.
        Assert.Contains("Bewertung:", html);
        Assert.Contains("ESBA (Erhöhung der Sicherheit an bestehenden Aufzügen):", html);
        Assert.Contains("Ersatzteilverfügbarkeit:", html);
        Assert.Contains("Art des Aufzugs:", html);
        Assert.Contains("Notruftechnologie:</b> GSM = über Funknetz, ANALOG = analoge Telefonleitung.", html);
    }

    [Fact]
    public void Blatt_dokument_ist_ein_querformat()
    {
        ProjectTableRow[] rows =
        [
            new()
            {
                TableKey = ProjectTableCatalog.ZustandsanalyseKey,
                Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["AssetName"] = "8609861" },
            },
        ];

        var html = ProjectTableSheet.RenderDocument(
            ProjectTableCatalog.Zustandsanalyse, rows, new PdfPageSettings("A4", true, 12));

        Assert.Contains("A4 landscape", html);
        Assert.Contains("margin: 12mm", html);
        Assert.Contains("8609861", html);
        // Als eigenes Blatt ohne Seitenumbruch am Anfang.
        Assert.DoesNotContain("page-break-before", html);
    }

    [Fact]
    public async Task Tabellenblaetter_stehen_im_querformat_im_pdf()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var renderer = new PageRenderer();
        var documents = new DocumentService(fixture.Database, session, renderer);

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var template = templates.Create(new TemplateInput
        {
            Name = "Bericht",
            Html = "<h1>{{project.name}}</h1>",
        });

        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey, [Row(("AssetName", "8609861"))]);
        tables.Save(project.Id, ProjectTableCatalog.InvestitionsplanungKey, [Row(("ModernMeasure", "Modernisierung"))]);

        var created = await documents.CreatePdfAsync(project.Id, template.Id, new PdfPageSettings());

        // Der Bericht bleibt im Hochformat, beide Tabellenblätter werden quer gedruckt.
        Assert.Equal(3, renderer.Calls.Count);
        Assert.False(renderer.Calls[0].Landscape);
        Assert.True(renderer.Calls[1].Landscape);
        Assert.True(renderer.Calls[2].Landscape);

        // Im PDF stehen die Seiten hintereinander: Bericht, Datenaufnahme, Investitionsplanung.
        using var merged = PdfReader.Open(created.FilePath, PdfDocumentOpenMode.Import);
        Assert.Equal(3, merged.PageCount);
        Assert.True(merged.Pages[0].Height > merged.Pages[0].Width);
        Assert.True(merged.Pages[1].Width > merged.Pages[1].Height);
        Assert.True(merged.Pages[2].Width > merged.Pages[2].Height);
    }

    [Fact]
    public void Ohne_zeilen_bleibt_das_dokument_ohne_zusatzblatt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new FakeRenderer());

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var template = templates.Create(new TemplateInput
        {
            Name = "Gutachten",
            Html = "<h1>{{project.name}}</h1>",
        });

        var html = documents.RenderHtml(project.Id, template.Id);

        Assert.DoesNotContain("page-break-before", html);
        Assert.Contains("Messligfeld 1 und 3, Sennewald", html);
    }
}
