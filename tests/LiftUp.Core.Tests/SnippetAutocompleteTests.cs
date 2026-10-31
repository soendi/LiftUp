using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class SnippetAutocompleteTests
{
    private static TextSnippet Snippet(string name, string? category = null, string html = "<p>Text</p>")
        => new() { Name = name, Category = category, Html = html };

    [Fact]
    public void Teilwort_im_namen_wird_gefunden()
    {
        var snippets = new[]
        {
            Snippet("Kündigung Textbaustein"),
            Snippet("Angebot"),
            Snippet("Rechnung"),
        };

        var matches = SnippetAutocomplete.Search(snippets, "baustein");

        Assert.Single(matches);
        Assert.Equal("Kündigung Textbaustein", matches[0].Name);
    }

    [Fact]
    public void Suche_ignoriert_gross_und_kleinschreibung()
    {
        var matches = SnippetAutocomplete.Search([Snippet("Widerrufsbelehrung")], "WIDERRUF");

        Assert.Single(matches);
    }

    [Fact]
    public void Kategorie_zaehlt_als_treffer()
    {
        var matches = SnippetAutocomplete.Search([Snippet("Angebot", "Verträge")], "vertr");

        Assert.Single(matches);
    }

    [Fact]
    public void Exakter_name_steht_vor_teilstring()
    {
        var matches = SnippetAutocomplete.Search(
            [Snippet("Angebot Unterlage"), Snippet("Angebot")],
            "Angebot");

        Assert.Equal("Angebot", matches[0].Name);
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void Ohne_treffer_bleibt_die_liste_leer()
    {
        var matches = SnippetAutocomplete.Search([Snippet("Angebot")], "xyz");

        Assert.Empty(matches);
    }

    [Fact]
    public void Archivierte_bausteine_werden_ausgeblendet()
    {
        var archived = Snippet("Angebot alt");
        archived.IsArchived = true;

        var matches = SnippetAutocomplete.Search([archived, Snippet("Angebot neu")], "Angebot");

        Assert.Single(matches);
        Assert.Equal("Angebot neu", matches[0].Name);
    }

    [Fact]
    public void Leere_suche_liefert_die_ersten_bausteine()
    {
        var snippets = Enumerable.Range(1, 12)
            .Select(index => Snippet($"Baustein {index:00}"))
            .ToArray();

        var matches = SnippetAutocomplete.Search(snippets, string.Empty);

        Assert.Equal(SnippetAutocomplete.DefaultLimit, matches.Count);
    }

    [Fact]
    public void Vorschau_entfernt_html_und_kuerzt_langen_text()
    {
        Assert.Equal("Guten Tag Muster AG", SnippetAutocomplete.Preview("<p>Guten&nbsp;Tag</p><p><strong>Muster</strong> AG</p>"));

        var preview = SnippetAutocomplete.Preview("<p>" + new string('a', 200) + "</p>", 20);
        Assert.Equal(20, preview.Length);
        Assert.EndsWith("…", preview);
    }
}
