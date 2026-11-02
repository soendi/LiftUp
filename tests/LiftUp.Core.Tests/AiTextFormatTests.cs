using LiftUp.Core.Ai;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class AiTextFormatTests
{
    [Fact]
    public void Absaetze_werden_zu_durch_Leerzeile_getrennten_Zeilen()
    {
        var text = AiTextFormat.ToPlainText("<p>Aggregat prüfen.</p><p>Zweite Zeile.</p>");

        Assert.Equal("Aggregat prüfen.\n\nZweite Zeile.", text);
    }

    [Fact]
    public void Zeilenumbruch_im_Absatz_bleibt_eine_Zeile()
    {
        var text = AiTextFormat.ToPlainText("<p>Erste Zeile<br />Zweite Zeile</p>");

        Assert.Equal("Erste Zeile\nZweite Zeile", text);
    }

    [Fact]
    public void Aufzaehlung_wird_zu_Punkten()
    {
        var text = AiTextFormat.ToPlainText("<ul><li>Erstens</li><li>Zweitens</li></ul>");

        Assert.Equal("• Erstens\n• Zweitens", text);
    }

    [Fact]
    public void Sonderzeichen_und_geschuetzte_Leerzeichen_kommen_als_Text_an()
    {
        var text = AiTextFormat.ToPlainText("<p>Ein&nbsp;Text &amp; mehr</p>");

        Assert.Equal("Ein Text & mehr", text);
    }

    [Fact]
    public void Leere_Absaetze_am_Rand_fallen_weg()
    {
        var text = AiTextFormat.ToPlainText("<p></p><p>Inhalt</p><p><br /></p>");

        Assert.Equal("Inhalt", text);
    }

    [Fact]
    public void Text_wird_wieder_zu_Absaetzen()
    {
        var html = AiTextFormat.ToHtml("Zeile 1\nZeile 2\n\nNächster Absatz");

        Assert.Equal("<p>Zeile 1<br />Zeile 2</p><p>Nächster Absatz</p>", html);
    }

    [Fact]
    public void Hin_und_zurueck_erhaelt_die_Gliederung()
    {
        const string text = "Erster Absatz\nZeile darunter\n\nZweiter Absatz";

        Assert.Equal(text, AiTextFormat.ToPlainText(AiTextFormat.ToHtml(text)));
    }

    [Fact]
    public void Html_im_Text_wird_unschaedlich_gemacht()
    {
        var html = AiTextFormat.ToHtml("<b>fett</b> & Co");

        Assert.Equal("<p>&lt;b&gt;fett&lt;/b&gt; &amp; Co</p>", html);
    }

    [Fact]
    public void Leerer_Inhalt_ergibt_leeren_Text()
    {
        Assert.Equal(string.Empty, AiTextFormat.ToPlainText(null));
        Assert.Equal(string.Empty, AiTextFormat.ToPlainText("<p></p>"));
        Assert.Equal(string.Empty, AiTextFormat.ToHtml(null));
    }
}
