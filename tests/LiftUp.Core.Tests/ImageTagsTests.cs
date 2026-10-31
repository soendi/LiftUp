using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class ImageTagsTests
{
    [Fact]
    public void Schlagworte_werden_getrennt_und_getrimmt()
    {
        var tags = ImageTags.Split(" logo , rot;;  grundriss ");

        Assert.Equal(["logo", "rot", "grundriss"], tags);
    }

    [Fact]
    public void Ohne_eintrag_gibt_es_keine_schlagworte()
    {
        Assert.Empty(ImageTags.Split(null));
        Assert.Empty(ImageTags.Split("   "));
    }

    [Fact]
    public void Ein_treffer_genuegt_bei_mehreren_gewaehlten_schlagworten()
    {
        var selected = new[] { "rot", "blau" };

        Assert.True(ImageTags.MatchesAny("logo, rot", selected));
        Assert.True(ImageTags.MatchesAny("blau", selected));
        Assert.False(ImageTags.MatchesAny("logo, gruen", selected));
        Assert.False(ImageTags.MatchesAny(null, selected));
    }

    [Fact]
    public void Leere_auswahl_filtert_nicht()
    {
        Assert.True(ImageTags.MatchesAny("logo", []));
        Assert.True(ImageTags.MatchesAny(null, []));
    }

    [Fact]
    public void Grossschreibung_spielt_keine_rolle()
    {
        Assert.True(ImageTags.MatchesAny("Logo, ROT", new[] { "rot" }));
    }
}
