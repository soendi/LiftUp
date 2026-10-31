using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class DocumentCompositionTests
{
    private const string TitlePage = """
        <!DOCTYPE html>
        <html lang="de">
        <head><style>body { font-family: Arial, sans-serif; }</style></head>
        <body><h1>Deckblatt</h1></body>
        </html>
        """;

    private static IReadOnlyList<DocumentSectionContent> Sections(params (string Title, string Html)[] items) =>
        items.Select(item => new DocumentSectionContent(item.Title, item.Html)).ToArray();

    private static int Count(string value, string part) =>
        value.Split(part, StringSplitOptions.None).Length - 1;

    [Fact]
    public void Erster_abschnitt_beginnt_auf_der_zweiten_seite()
    {
        var html = DocumentComposition.Build(TitlePage, Sections(("Einleitung", "<p>Text</p>")));

        Assert.Equal(1, Count(html, "liftup-section-first\">"));
        Assert.Contains("break-before: page", html);
        Assert.DoesNotContain("style=\"page-break-before", html);
    }

    [Fact]
    public void Weitere_abschnitte_bleiben_im_fluss()
    {
        var html = DocumentComposition.Build(TitlePage, Sections(
            ("Erster", "<p>eins</p>"),
            ("Zweiter", "<p>zwei</p>"),
            ("Dritter", "<p>drei</p>")));

        Assert.Equal(3, Count(html, "<section class=\"liftup-section"));
        Assert.Equal(1, Count(html, "liftup-section-first\">"));
        Assert.Contains("<section class=\"liftup-section\"><div class=\"liftup-section-start\"><h2>Zweiter</h2>", html);
    }

    [Fact]
    public void Ueberschrift_bleibt_mit_dem_ersten_absatz_zusammen()
    {
        var html = DocumentComposition.Build(TitlePage, Sections(
            ("Einleitung", "<p>Erster Absatz</p><p>Zweiter Absatz</p>")));

        Assert.Contains(
            "<div class=\"liftup-section-start\"><h2>Einleitung</h2><p>Erster Absatz</p></div><p>Zweiter Absatz</p>",
            html);
    }

    [Fact]
    public void Verschachtelte_auszeichnung_bleibt_im_ersten_block()
    {
        var html = DocumentComposition.Build(TitlePage, Sections(
            ("Einleitung", "<p>Text mit <strong>Fett</strong> und <img src=\"x\" /></p><p>Rest</p>")));

        Assert.Contains("<p>Text mit <strong>Fett</strong> und <img src=\"x\" /></p></div><p>Rest</p>", html);
    }

    [Fact]
    public void Ueberschrift_ohne_inhalt_und_leere_abschnitte_bleiben_sauber()
    {
        var html = DocumentComposition.Build(TitlePage, Sections(
            ("Leer", "   "),
            ("", "<p>ohne Titel</p>"),
            ("Mit Titel", "<p>Text</p>")));

        Assert.Equal(1, Count(html, "liftup-section-first\">"));
        Assert.Contains("<section class=\"liftup-section liftup-section-first\"><p>ohne Titel</p></section>", html);
    }

    [Fact]
    public void Regeln_fuer_abstand_und_zeilenumbruch_stehen_im_kopf()
    {
        var html = DocumentComposition.Build(TitlePage, Sections(("A", "<p>a</p>")));

        Assert.Contains("margin-top: 7mm", html);
        Assert.Contains("break-inside: avoid", html);
        Assert.Contains("orphans: 2", html);
        Assert.True(
            html.IndexOf("liftup-section", StringComparison.Ordinal) < html.IndexOf("</head>", StringComparison.Ordinal),
            "Die Regeln müssen im Kopf stehen.");
    }

    [Fact]
    public void Sehr_langer_erster_block_wird_nicht_zusammengehalten()
    {
        var longBlock = "<p>" + new string('x', 2000) + "</p>";
        var html = DocumentComposition.Build(TitlePage, Sections(("A", longBlock + "<p>Rest</p>")));

        Assert.Contains("<section class=\"liftup-section liftup-section-first\"><h2>A</h2>", html);
        Assert.DoesNotContain("class=\"liftup-section-start\"", html);
    }

    [Fact]
    public void Ohne_abschnitte_bleibt_die_titelseite_unveraendert()
    {
        var html = DocumentComposition.Build(TitlePage, []);

        Assert.Equal(TitlePage, html);
    }
}
