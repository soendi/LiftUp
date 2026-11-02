using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class ProjectVariantNamesTests
{
    [Theory]
    [InlineData(2026, 1, 15)]
    [InlineData(2026, 6, 30)]
    public void Bis_Ende_Juni_gilt_das_laufende_Jahr(int year, int month, int day)
    {
        var today = new DateOnly(year, month, day);

        Assert.Equal("Modernisierung 2026", ProjectVariantNames.DefaultModernisierung(today));
        Assert.Equal("Ersatzanlage 2026-2027", ProjectVariantNames.DefaultErsatzanlage(today));
    }

    [Theory]
    [InlineData(2026, 7, 1)]
    [InlineData(2026, 11, 2)]
    public void Ab_Juli_ruecken_die_Jahre_nach(int year, int month, int day)
    {
        var today = new DateOnly(year, month, day);

        Assert.Equal("Modernisierung 2026-2027", ProjectVariantNames.DefaultModernisierung(today));
        Assert.Equal("Ersatzanlage 2027-2028", ProjectVariantNames.DefaultErsatzanlage(today));
    }

    [Fact]
    public void Kopf_bleibt_Variante_1_und_Variante_2()
    {
        Assert.Equal("Variante 1 – Modernisierung 2026-2027", ProjectVariantNames.Header(1, " Modernisierung 2026-2027 "));
        Assert.Equal("Variante 2 – Ersatzanlage 2027-2028", ProjectVariantNames.Header(2, "Ersatzanlage 2027-2028"));
        Assert.Equal("Variante 1", ProjectVariantNames.Header(1, " "));
    }

    [Fact]
    public void Eigene_Benennung_ersetzt_den_Variantenkopf_der_Investitionsplanung()
    {
        var today = new DateOnly(2026, 11, 2);
        var definition = ProjectTableCatalog.WithVariantLabels(
            ProjectTableCatalog.Investitionsplanung, "Instandsetzung 2027", null, today);

        Assert.Contains(definition.Columns, column => column.Group == "Variante 1 – Instandsetzung 2027");
        // Ohne eigenen Text bleibt der Standard.
        Assert.Contains(definition.Columns, column => column.Group == "Variante 2 – Ersatzanlage 2027-2028");
    }

    [Fact]
    public void Andere_Tabellen_bleiben_unveraendert()
    {
        var definition = ProjectTableCatalog.WithVariantLabels(
            ProjectTableCatalog.Zustandsanalyse, "egal", "egal", new DateOnly(2026, 11, 2));

        Assert.Same(ProjectTableCatalog.Zustandsanalyse, definition);
    }
}
