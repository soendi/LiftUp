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

    private static ProjectAnalysisTextInput Text(string sectionKey, string title, string html) =>
        new(sectionKey, title, html);

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
            Text("Aggregat", "Aggregat", "<p>Aggregat in Ordnung.</p>"),
            Text("Tragmittel", "Hubzylinder / Tragmittel", "<p>Seile leicht verschlissen.</p>"),
            Text("Steuerung", string.Empty, "   "),
        ]);

        // Leere Texte ohne Untertitel werden nicht gespeichert.
        Assert.Equal(2, saved.Count);
        Assert.Empty(analysis.Get(second.Id));

        var loaded = analysis.Get(first.Id);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(
            "<p>Seile leicht verschlissen.</p>",
            loaded.Single(text => text.SectionKey == "Tragmittel").Html);
        Assert.Equal("Hubzylinder / Tragmittel", loaded.Single(text => text.SectionKey == "Tragmittel").Title);

        // Erneutes Speichern ersetzt den bisherigen Stand.
        analysis.Save(first.Id, [Text("Aggregat", "Aggregat", "<p>Ersetzt.</p>")]);
        Assert.Equal("Aggregat", Assert.Single(analysis.Get(first.Id)).SectionKey);
    }

    [Fact]
    public void Neue_analysepunkte_kommen_hinzu_und_behalten_ihre_reihenfolge()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));

        var saved = analysis.Save(project.Id,
        [
            Text("Aggregat", "Aggregat", "<p>Aggregat.</p>"),
            Text("P7", "Dach", "<p>Dach geprüft.</p>"),
        ]);

        // Auch ein Punkt, den die Vorlage nicht kennt, wird gespeichert.
        Assert.Equal(2, saved.Count);
        Assert.Equal(
            ["2.1 Aggregat", "2.2 Dach"],
            ProjectAnalysisCatalog.SectionsFor(analysis.Get(project.Id)).Select(section => section.Heading));

        // Ein Untertitel ohne Text bleibt als Punkt erhalten.
        analysis.Save(project.Id, [Text("P8", "Nur Untertitel", string.Empty)]);
        var single = Assert.Single(analysis.Get(project.Id));
        Assert.Equal("Nur Untertitel", single.Title);
        Assert.Equal(string.Empty, single.Html);
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
            Text("Aggregat", "Aggregat", "<p>Aggregat läuft ruhig.</p>"),
            Text("Kabine", "Kabine / Triebmechanik", "<p>Kabine ohne Mängel.</p>"),
        ]);

        var html = documents.RenderHtml(project.Id, template.Id);

        Assert.Contains("2. Analyse", html);
        Assert.Contains("2.1 Aggregat", html);
        Assert.Contains("Aggregat läuft ruhig.", html);
        Assert.Contains("2.2 Kabine / Triebmechanik", html);
        // Nicht erfasste Punkte gibt es nicht.
        Assert.DoesNotContain("2.3", html);
    }

    [Fact]
    public void Leerzeilen_und_leere_absaetze_am_ende_fallen_beim_speichern_weg()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        analysis.Save(project.Id,
        [
            Text("Aggregat", "Aggregat", "test\n\n\n"),
            Text("Tragmittel", "Hubzylinder / Tragmittel", "<p>Hubzylinder dicht.</p><p><br></p><p>&nbsp;</p>"),
        ]);

        var texts = analysis.Get(project.Id).ToDictionary(text => text.SectionKey, text => text.Html);
        Assert.Equal("test", texts["Aggregat"]);
        Assert.Equal("<p>Hubzylinder dicht.</p>", texts["Tragmittel"]);
    }

    [Fact]
    public void Untertitel_und_text_haben_ueberall_denselben_abstand()
    {
        var html = ProjectAnalysisCatalog.Render(
        [
            new ProjectAnalysisText
            {
                SectionKey = "Aggregat",
                Title = "Aggregat",
                Html = "<p style=\"margin:1em 0;\">Aggregat.</p>",
            },
            new ProjectAnalysisText { SectionKey = "Fazit", Html = "<p>Ersatz empfohlen.</p>" },
        ]);

        // Der obere Rand des ersten Blocks entfällt – den Abstand bestimmt der Untertitel.
        Assert.Contains(".liftup-analysis>:first-child{margin-top:0 !important;}", html);
        // Die Regel steht einmal im Dokument, obwohl zwei Kapitel folgen.
        Assert.Equal(1, html.Split("<style>").Length - 1);
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
            Text("Aggregat", "Aggregat", "<p>Aggregat.</p>"),
            Text("Tragmittel", "Hubzylinder / Tragmittel", "<p>Hubzylinder dicht.</p>"),
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
        // Die Punkte stehen nicht fest – sie kommen aus den erfassten Texten.
        Assert.Empty(chapter.Sections);

        var texts = new List<ProjectAnalysisText>
        {
            new() { SectionKey = "Aggregat", Title = "Aggregat", SortOrder = 0, Html = "<p>Text.</p>" },
            new() { SectionKey = "Tragmittel", Title = "Hubzylinder / Tragmittel", SortOrder = 1 },
        };

        var sections = ProjectAnalysisCatalog.SectionsFor(texts);
        Assert.Equal(2, sections.Count);
        Assert.Equal("2.1 Aggregat", sections[0].Heading);
        Assert.Equal("2.2 Hubzylinder / Tragmittel", sections[1].Heading);

        // Der Reiter „2. Analyse“ der Vorlage wird erkannt, „3. Fazit“ nicht.
        Assert.True(ProjectAnalysisCatalog.IsChapterTitle("2. Analyse"));
        Assert.True(ProjectAnalysisCatalog.IsChapterTitle("Analyse"));
        Assert.False(ProjectAnalysisCatalog.IsChapterTitle("3. Fazit / Empfehlung"));

        // Im Reiter „2. Analyse“ steht der Haupttitel schon als Überschrift.
        var withoutTitle = ProjectAnalysisCatalog.Render(texts, includeChapterTitle: false);
        Assert.Contains("2.1 Aggregat", withoutTitle);
        Assert.DoesNotContain("2. Analyse", withoutTitle);

        Assert.Equal("Tragmittel", ProjectAnalysisCatalog.FindSection(texts, "2.2")?.Key);
        Assert.Equal("Tragmittel", ProjectAnalysisCatalog.FindSection(texts, "Hubzylinder / Tragmittel")?.Key);
        Assert.Null(ProjectAnalysisCatalog.FindSection(texts, "9.9"));
    }

    [Fact]
    public void Ohne_untertitel_steht_nur_die_nummer_im_reiter()
    {
        var texts = new List<ProjectAnalysisText>
        {
            new() { SectionKey = "P1", Html = "<p>Text.</p>" },
        };

        var section = Assert.Single(ProjectAnalysisCatalog.SectionsFor(texts));
        Assert.Equal("2.1", section.Heading);

        // Ohne Untertitel steht keine Zwischenüberschrift im Bericht.
        var html = ProjectAnalysisCatalog.Render(texts);
        Assert.DoesNotContain("2.1", html);
        Assert.Contains("Text.", html);
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
        Assert.Equal("Fazit", ProjectAnalysisCatalog.FindSection([], "3.1")?.Key);
    }

    [Fact]
    public void Fazit_wird_gespeichert_und_mit_seinem_haupttitel_ausgegeben()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        analysis.Save(project.Id, [Text("Fazit", string.Empty, "<p>Modernisierung empfohlen.</p>")]);

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
