using LiftUp.Core.Documents;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class PlaceholderRemovalTests
{
    [Fact]
    public void Entfernt_fehlende_bilder_und_textbausteine()
    {
        const string html =
            "<p>{{bild:Logo}}</p><p>{{bild:Stempel}}</p><p>{{snippet:Garantie}}</p><p>{{project.name}}</p>";

        var result = PlaceholderRenderer.RemovePlaceholders(html, ["Logo"], ["Garantie"]);

        Assert.DoesNotContain("Logo", result);
        Assert.DoesNotContain("Garantie", result);
        Assert.Contains("{{bild:Stempel}}", result);
        Assert.Contains("{{project.name}}", result);
    }

    [Fact]
    public void Entfernt_englische_bildplatzhalter_und_ignoriert_schreibweise()
    {
        const string html = "{{ IMAGE : logo }}<p>{{Bild:Stempel}}</p><p>{{SNIPPET:Garantie}}</p>";

        var result = PlaceholderRenderer.RemovePlaceholders(html, ["LOGO", "stempel"], ["garantie"]);

        Assert.DoesNotContain("logo", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stempel", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("garantie", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unbekannte_namen_bleiben_stehen()
    {
        const string html = "{{bild:Logo}}";

        Assert.Equal(html, PlaceholderRenderer.RemovePlaceholders(html, ["Anderes"], ["Garantie"]));
    }

    [Fact]
    public void Ohne_fehlende_angaben_bleibt_der_text_unveraendert()
    {
        const string html = "{{bild:Logo}}";

        Assert.Equal(html, PlaceholderRenderer.RemovePlaceholders(html, [], []));
    }
}
