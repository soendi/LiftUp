using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class ImageFormatTests
{
    private const string Image = "<img src=\"data:image/png;base64,AA==\" alt=\"Grundriss\" />";

    [Fact]
    public void Bild_wird_mit_klassen_daten_und_unterschrift_gerahmt()
    {
        var format = new ImageFormat(ImageSize.Large, ImagePlacement.Left, ImageFlow.Square, "Der Grundriss");

        var html = ImageFormat.Wrap(Image, format);

        Assert.StartsWith("<figure class=\"liftup-fig liftup-fig-large liftup-fig-left liftup-fig-square liftup-fig-captioned\"", html);
        Assert.Contains("data-liftup-size=\"large\"", html);
        Assert.Contains("data-liftup-place=\"left\"", html);
        Assert.Contains("data-liftup-flow=\"square\"", html);
        Assert.Contains(Image, html);
        Assert.Contains("<figcaption>Der Grundriss</figcaption>", html);
        Assert.EndsWith("</figure>", html);
    }

    [Fact]
    public void Ohne_unterschrift_entsteht_keine_figcaption()
    {
        var html = ImageFormat.Wrap(Image);

        Assert.DoesNotContain("figcaption", html);
        Assert.DoesNotContain("liftup-fig-captioned", html);
        Assert.Contains("liftup-fig-medium", html);
        Assert.Contains("liftup-fig-inline", html);
    }

    [Fact]
    public void Unterschrift_wird_escaped()
    {
        var html = ImageFormat.Wrap(Image, new ImageFormat(Caption: "Plan <A> & B"));

        Assert.Contains("<figcaption>Plan &lt;A&gt; &amp; B</figcaption>", html);
    }

    [Theory]
    [InlineData(ImageSize.Small, ImagePlacement.Right, ImageFlow.TopAndBottom)]
    [InlineData(ImageSize.Medium, ImagePlacement.Center, ImageFlow.Inline)]
    [InlineData(ImageSize.Large, ImagePlacement.BelowText, ImageFlow.Through)]
    public void Angaben_werden_aus_den_attributen_wieder_gelesen(ImageSize size, ImagePlacement placement, ImageFlow flow)
    {
        var original = new ImageFormat(size, placement, flow, "Unterschrift");

        var parsed = ImageFormat.Parse(original.SizeToken, original.PlacementToken, original.FlowToken, "Unterschrift");

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Unbekannte_angaben_fallen_auf_die_vorgabe_zurueck()
    {
        var parsed = ImageFormat.Parse(null, "irgendwas", "", "   ");

        Assert.Equal(ImageFormat.Default, parsed);
    }

    [Theory]
    [InlineData("small", "33.3333%")]
    [InlineData("medium", "50%")]
    [InlineData("large", "66.6667%")]
    public void Groessen_teilen_die_zeile_in_drittel(string token, string width)
    {
        Assert.Contains(".liftup-fig-" + token + " { width: " + width + "; }", ImageFormat.Css);
    }

    [Fact]
    public void Dokument_bringt_die_bildregeln_mit()
    {
        var html = DocumentComposition.Build(
            "<html><head></head><body><h1>Deckblatt</h1></body></html>",
            [new DocumentSectionContent("Plan", ImageFormat.Wrap(Image, new ImageFormat(Flow: ImageFlow.Square, Placement: ImagePlacement.Left)))]);

        Assert.Contains(".liftup-fig-strip", html);
        Assert.Contains(".liftup-fig-square.liftup-fig-left", html);

        // Bild und Unterschrift bilden einen Block, sobald eine Unterschrift dranhängt.
        Assert.Contains(".liftup-fig-captioned.liftup-fig-inline { display: block; }", html);
        Assert.Contains("object-fit: contain", html);
        Assert.True(
            html.IndexOf(".liftup-fig-strip", StringComparison.Ordinal) < html.IndexOf("</head>", StringComparison.Ordinal),
            "Die Bildregeln müssen im Kopf stehen.");
    }
}
