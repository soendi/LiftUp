using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Erfasste Texte der Analyse je Projekt und ihre Platzhalter im Dokument.</summary>
public sealed class ProjectAnalysisTests
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

    private static ProjectAnalysisTextInput Text(string sectionKey, string html) => new(sectionKey, html);

    [Fact]
    public void Analyse_wird_je_projekt_gespeichert_und_gelesen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);

        var first = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var second = projects.Create(Input(Path.Combine(fixture.Root, "p2")));

        var saved = analysis.Save(first.Id,
        [
            Text("Aggregat", "<p>Aggregat in Ordnung.</p>"),
            Text("Tragmittel", "<p>Seile leicht verschlissen.</p>"),
            Text("Steuerung", "   "),
        ]);

        // Leere Texte werden nicht gespeichert.
        Assert.Equal(2, saved.Count);
        Assert.Empty(analysis.Get(second.Id));

        var loaded = analysis.Get(first.Id);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(
            "<p>Seile leicht verschlissen.</p>",
            loaded.Single(text => text.SectionKey == "Tragmittel").Html);

        // Erneutes Speichern ersetzt den bisherigen Stand.
        analysis.Save(first.Id, [Text("Aggregat", "<p>Ersetzt.</p>")]);
        Assert.Equal("Aggregat", Assert.Single(analysis.Get(first.Id)).SectionKey);
    }

    [Fact]
    public void Unbekannter_analysepunkt_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));

        Assert.Throws<ValidationException>(() => analysis.Save(project.Id, [Text("Dach", "<p>…</p>")]));
    }

    [Fact]
    public void Analyse_erscheint_als_platzhalter_im_dokument()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new FakeRenderer());

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var template = templates.Create(new TemplateInput
        {
            Name = "Gutachten",
            Html = "<h1>{{project.name}}</h1>{{analyse}}",
        });

        analysis.Save(project.Id,
        [
            Text("Aggregat", "<p>Aggregat läuft ruhig.</p>"),
            Text("Kabine", "<p>Kabine ohne Mängel.</p>"),
        ]);

        var html = documents.RenderHtml(project.Id, template.Id);

        Assert.Contains("2. Analyse", html);
        Assert.Contains("2.1 Aggregat", html);
        Assert.Contains("Aggregat läuft ruhig.", html);
        Assert.Contains("2.6 Kabine / Triebmechanik", html);
        // Nicht erfasste Unterpunkte stehen nicht im Dokument.
        Assert.DoesNotContain("2.3 Steuerung / Regler", html);
    }

    [Fact]
    public void Einzelner_unterpunkt_laesst_sich_einsetzen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new FakeRenderer());

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var template = templates.Create(new TemplateInput
        {
            Name = "Gutachten",
            Html = "<h1>Kurzbericht</h1>{{analyse:Tragmittel}}",
        });

        analysis.Save(project.Id,
        [
            Text("Aggregat", "<p>Aggregat.</p>"),
            Text("Tragmittel", "<p>Hubzylinder dicht.</p>"),
        ]);

        var html = documents.RenderHtml(project.Id, template.Id);

        Assert.Contains("2.2 Hubzylinder / Tragmittel", html);
        Assert.Contains("Hubzylinder dicht.", html);
        Assert.DoesNotContain("2.1 Aggregat", html);
    }

    [Fact]
    public void Haupttitel_hat_unterpunkte_mit_nummern()
    {
        var chapter = ProjectAnalysisCatalog.Analyse;

        Assert.Equal("2. Analyse", chapter.Heading);
        Assert.Equal(7, chapter.Sections.Count);
        Assert.Equal("2.1 Aggregat", chapter.Sections[0].Heading);
        Assert.Equal("2.2 Hubzylinder / Tragmittel", chapter.Sections[1].Heading);

        // Der Reiter „2. Analyse“ der Vorlage wird erkannt, „3. Fazit“ nicht.
        Assert.True(ProjectAnalysisCatalog.IsChapterTitle("2. Analyse"));
        Assert.True(ProjectAnalysisCatalog.IsChapterTitle("Analyse"));
        Assert.False(ProjectAnalysisCatalog.IsChapterTitle("3. Fazit / Empfehlung"));

        // Im Reiter „2. Analyse“ steht der Haupttitel schon als Überschrift.
        var withoutTitle = ProjectAnalysisCatalog.Render(
            [new ProjectAnalysisText { SectionKey = "Aggregat", Html = "<p>Text.</p>" }],
            includeChapterTitle: false);
        Assert.Contains("2.1 Aggregat", withoutTitle);
        Assert.DoesNotContain("2. Analyse", withoutTitle);

        Assert.Equal("Tragmittel", ProjectAnalysisCatalog.FindSection("2.2")?.Key);
        Assert.Equal("Steuerung", ProjectAnalysisCatalog.FindSection("Steuerung / Regler")?.Key);
        Assert.Null(ProjectAnalysisCatalog.FindSection("9.9"));
    }

    [Fact]
    public void Haupttitel_folgen_dem_bericht_mit_anlagedaten_und_fazit()
    {
        Assert.Equal(
            ["1. Anlagedaten", "2. Analyse", "3. Fazit / Empfehlung"],
            ProjectAnalysisCatalog.All.Select(chapter => chapter.Heading));

        // „1. Anlagedaten“ ist die Tabelle der Zustandsanalyse, kein Text.
        Assert.Equal(ProjectTableCatalog.ZustandsanalyseKey, ProjectAnalysisCatalog.Anlagedaten.TableKey);
        Assert.Empty(ProjectAnalysisCatalog.Anlagedaten.Sections);

        Assert.Equal("3.1 Fazit und Empfehlung", Assert.Single(ProjectAnalysisCatalog.Fazit.Sections).Heading);
        Assert.Equal("Fazit", ProjectAnalysisCatalog.FindSection("3.1")?.Key);
    }

    [Fact]
    public void Fazit_wird_gespeichert_und_mit_seinem_haupttitel_ausgegeben()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        analysis.Save(project.Id, [Text("Fazit", "<p>Modernisierung empfohlen.</p>")]);

        var html = ProjectAnalysisCatalog.Render(analysis.Get(project.Id));

        Assert.Contains("3. Fazit / Empfehlung", html);
        Assert.Contains("Modernisierung empfohlen.", html);

        // Der einzige Punkt des Haupttitels steht ohne eigene Zwischenüberschrift darunter.
        Assert.DoesNotContain("3.1", html);

        var single = ProjectAnalysisCatalog.Render(analysis.Get(project.Id), "3.1");
        Assert.DoesNotContain("3.1", single);
        Assert.Contains("Modernisierung empfohlen.", single);
    }
}
