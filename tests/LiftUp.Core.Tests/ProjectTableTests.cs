using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
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
