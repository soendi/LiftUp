using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Fotos der Analyse je Aufzug und die eigene Fotoseite im PDF.</summary>
public sealed class ProjectAnalysisPhotoTests
{
    /// <summary>Kleinstes gültiges PNG (1×1, transparent).</summary>
    private const string PixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private sealed class FakeRenderer : IHtmlToPdfRenderer
    {
        /// <summary>Alle HTML-Stände, die gedruckt werden sollten – für Prüfungen am Inhalt.</summary>
        public List<string> Calls { get; } = [];

        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default)
        {
            Calls.Add(html);
            return Task.FromResult(Encoding.UTF8.GetBytes("%PDF-1.4 " + html.Length));
        }
    }

    private static ProjectInput Input(string folder) => new()
    {
        Name = "Messligfeld 1 und 3, Sennewald",
        FolderPath = folder,
        Status = ProjectStatus.Active,
        CreateFolderIfMissing = true,
    };

    private static string WritePhoto(Project project, string name = "foto.png")
    {
        var folder = Path.Combine(project.FolderPath, ProjectFolders.Images);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Convert.FromBase64String(PixelPng));
        return path;
    }

    private static PhotoAnnotation Circle() => new()
    {
        Kind = PhotoAnnotationKind.Circle,
        X1 = 120,
        Y1 = 90,
        Radius = 40,
    };

    private static PhotoAnnotation Arrow() => new()
    {
        Kind = PhotoAnnotationKind.Arrow,
        X1 = 30,
        Y1 = 40,
        X2 = 90,
        Y2 = 120,
    };

    [Fact]
    public void Fotos_haengen_am_aufzug_und_behalten_ihre_markierungen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var photo = WritePhoto(project);

        var saved = analysis.SavePhotos(project.Id, "8609861",
        [
            new ProjectAnalysisPhotoInput(photo, "Verschlissene Seile", [Circle(), Arrow()], 400, 300),
            new ProjectAnalysisPhotoInput(photo, "", [], 400, 300),
        ]);

        Assert.Equal(2, saved.Count);
        Assert.Empty(analysis.GetPhotos(project.Id).Where(item => item.LiftKey == "8609862"));

        var loaded = analysis.GetPhotos(project.Id);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("8609861", loaded[0].LiftKey);
        Assert.Equal("Verschlissene Seile", loaded[0].Caption);
        Assert.Equal(2, loaded[0].Annotations.Count);
        Assert.Equal(40, loaded[0].Annotations[0].Radius);
        Assert.Equal(PhotoAnnotationKind.Arrow, loaded[0].Annotations[1].Kind);

        // Ein zweiter Aufzug ändert die Fotos des ersten nicht.
        analysis.SavePhotos(project.Id, "8609862", [new ProjectAnalysisPhotoInput(photo, "", [], 400, 300)]);
        Assert.Equal(2, analysis.GetPhotos(project.Id).Count(item => item.LiftKey == "8609861"));
    }

    [Fact]
    public void Aufzuege_kommen_aus_der_zustandsanalyse()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        Assert.Empty(analysis.GetLifts(project.Id));

        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
        [
            new ProjectTableRowInput(new Dictionary<string, string>
            {
                ["AssetName"] = "8609861",
                ["Location"] = "Messligfeldstrasse 1",
            }),
            new ProjectTableRowInput(new Dictionary<string, string>
            {
                ["AssetName"] = "8609862",
                ["Location"] = "Messligfeldstrasse 3",
            }),
            new ProjectTableRowInput(new Dictionary<string, string> { ["Location"] = "Ohne Nummer" }),
            new ProjectTableRowInput(new Dictionary<string, string> { ["AssetName"] = "8609863" }),
        ]);

        var lifts = analysis.GetLifts(project.Id);
        Assert.Equal(["8609861", "8609862"], lifts.Select(lift => lift.Name));
        Assert.Equal("Messligfeldstrasse 1", lifts[0].Location);
        Assert.Equal("Messligfeldstrasse 3", lifts[1].Location);
    }

    [Fact]
    public void Jede_zeile_der_zustandsanalyse_bekommt_einen_foto_reiter()
    {
        Assert.Equal("4.1 8609861 Messligfeldstrasse 1",
            ProjectAnalysisPhotoCatalog.LiftHeading(0, "8609861", "Messligfeldstrasse 1"));
        Assert.Equal("4.2 8609862", ProjectAnalysisPhotoCatalog.LiftHeading(1, "8609862", "  "));
        Assert.Equal("4.3 Lift Turnhalle", ProjectAnalysisPhotoCatalog.LiftHeading(2, "  Lift Turnhalle  "));
    }

    [Fact]
    public void Fotoseite_zeigt_ein_raster_mit_markierungen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var photo = WritePhoto(project);

        ProjectAnalysisPhoto[] photos =
        [
            new()
            {
                LiftKey = "8609861",
                FilePath = photo,
                Caption = "Verschlissene Seile",
                ImageWidth = 400,
                ImageHeight = 300,
                Annotations = [Circle(), Arrow()],
            },
        ];

        var html = ProjectPhotoSheet.Render(
            photos,
            lifts: [new ProjectAnalysisLift("8609861", "Messligfeldstrasse 1")]);

        Assert.Contains("4. Fotos", html);
        Assert.Contains("4.1 8609861 Messligfeldstrasse 1", html);
        Assert.Contains("grid-template-columns:1fr 1fr", html);
        Assert.Contains("Verschlissene Seile", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("<circle", html);
        Assert.Contains("<polygon", html);
        Assert.Contains("viewBox=\"0 0 400 300\"", html);
    }

    [Fact]
    public void Fotos_stehen_als_eigene_seite_im_dokument()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new FakeRenderer());

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var photo = WritePhoto(project);
        var template = templates.Create(new TemplateInput
        {
            Name = "Gutachten",
            Html = "<h1>{{project.name}}</h1>",
        });

        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
        [
            new ProjectTableRowInput(new Dictionary<string, string>
            {
                ["AssetName"] = "8609861",
                ["Location"] = "Messligfeldstrasse 1",
            }),
        ]);

        analysis.SavePhotos(project.Id, "8609861",
            [new ProjectAnalysisPhotoInput(photo, "Türe", [Circle()], 400, 300)]);

        var html = documents.RenderHtml(project.Id, template.Id);

        Assert.Contains("4.1 8609861", html);
        Assert.Contains("page-break-before: always", html);
        Assert.Contains("Türe", html);
    }

    [Fact]
    public void Aufzug_ohne_fotos_zaehlt_in_der_nummerierung_mit()
    {
        ProjectAnalysisPhoto photo = new()
        {
            LiftKey = "8609862",
            FilePath = "C:\\bilder\\foto.png",
        };

        var html = ProjectPhotoSheet.Render(
            [photo],
            pageBreak: false,
            lifts:
            [
                new ProjectAnalysisLift("8609861", "Messligfeldstrasse 1"),
                new ProjectAnalysisLift("8609862", "Messligfeldstrasse 3"),
            ]);

        // Der erste Aufzug hat keine Fotos, der zweite behält seine Nummer 4.2.
        Assert.Contains("4.2 8609862 Messligfeldstrasse 3", html);
        Assert.DoesNotContain("4.1", html);
    }

    [Fact]
    public void Fotos_ohne_aufzug_bleiben_aus_dem_dokument()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new FakeRenderer());

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var photo = WritePhoto(project);
        var template = templates.Create(new TemplateInput
        {
            Name = "Gutachten",
            Html = "<h1>{{project.name}}</h1>",
        });

        // Foto eines früheren Aufzugs, der in der Zustandsanalyse nicht mehr steht.
        analysis.SavePhotos(project.Id, "8609861",
            [new ProjectAnalysisPhotoInput(photo, "Altes Foto", [], 400, 300)]);

        Assert.DoesNotContain("4. Fotos", documents.RenderHtml(project.Id, template.Id));

        // Ein anderer Aufzug bekommt einen Reiter, das alte Foto bleibt trotzdem weg.
        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
        [
            new ProjectTableRowInput(new Dictionary<string, string>
            {
                ["AssetName"] = "8609862",
                ["Location"] = "Messligfeldstrasse 3",
            }),
        ]);

        var html = documents.RenderHtml(project.Id, template.Id);
        Assert.DoesNotContain("4. Fotos", html);
        Assert.DoesNotContain("Altes Foto", html);
    }

    [Fact]
    public void Bericht_enthaelt_anlagedaten_analyse_und_fazit_vor_den_fotos()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new FakeRenderer());

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var photo = WritePhoto(project);
        var template = templates.Create(new TemplateInput
        {
            Name = "Gutachten",
            Html = "<h1>{{project.name}}</h1>",
        });

        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
        [
            new ProjectTableRowInput(new Dictionary<string, string>
            {
                ["AssetName"] = "8609861",
                ["Location"] = "Messligfeldstrasse 1",
            }),
        ]);

        tables.Save(project.Id, ProjectTableCatalog.AnlagedatenKey,
        [
            new ProjectTableRowInput(new Dictionary<string, string>
            {
                ["AssetName"] = "8609861",
                ["Make"] = "Schindler",
            }),
        ]);

        analysis.Save(project.Id,
        [
            new ProjectAnalysisTextInput("Aggregat", "<p>Aggregat geprüft.</p>"),
            new ProjectAnalysisTextInput("Fazit", "<p>Ersatz empfohlen.</p>"),
        ]);

        analysis.SavePhotos(project.Id, "8609861",
            [new ProjectAnalysisPhotoInput(photo, "Türe", [], 400, 300)]);

        var html = documents.RenderHtml(project.Id, template.Id);

        // Die drei erfassten Punkte stehen im Dokument.
        Assert.Contains("1. Anlagedaten", html);
        Assert.Contains("Schindler", html);
        Assert.Contains("2. Analyse", html);
        Assert.Contains("Aggregat geprüft.", html);
        Assert.Contains("3. Fazit / Empfehlung", html);
        Assert.Contains("Ersatz empfohlen.", html);

        // Und zwar nach den Tabellenblättern, aber vor den Fotos.
        Assert.True(
            html.IndexOf("Datenaufnahme der Aufzüge", StringComparison.Ordinal)
            < html.IndexOf("1. Anlagedaten", StringComparison.Ordinal),
            "Die komplette Analyse muss nach den Tabellenblättern stehen.");
        Assert.True(
            html.IndexOf("3. Fazit / Empfehlung", StringComparison.Ordinal)
            < html.IndexOf("4. Fotos", StringComparison.Ordinal),
            "Anlagedaten, Analyse und Fazit müssen vor den Fotos stehen.");
    }

    [Fact]
    public async Task Fazit_steht_auch_im_erzeugten_pdf()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);
        var renderer = new FakeRenderer();
        var documents = new DocumentService(fixture.Database, session, renderer);

        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var template = templates.Create(new TemplateInput
        {
            Name = "Gutachten",
            Html = "<h1>{{project.name}}</h1>",
        });

        analysis.Save(project.Id, [new ProjectAnalysisTextInput("Fazit", "<p>Ersatz empfohlen.</p>")]);

        await documents.CreatePdfAsync(project.Id, template.Id);

        // Der Weg über „PDF erzeugen“ druckt das Fazit mit.
        Assert.Contains(renderer.Calls, html => html.Contains("3. Fazit / Empfehlung", StringComparison.Ordinal));
        Assert.Contains(renderer.Calls, html => html.Contains("Ersatz empfohlen.", StringComparison.Ordinal));
    }

    [Fact]
    public void Ohne_fotos_bleibt_das_dokument_ohne_fotoseite()
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

        Assert.DoesNotContain("4. Fotos", html);
        Assert.DoesNotContain("page-break-before", html);
    }
}
