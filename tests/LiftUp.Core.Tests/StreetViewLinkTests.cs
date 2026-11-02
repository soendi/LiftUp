using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Adresse und Kartenadresse für die Straßenansicht aus den Projektdaten.</summary>
public class StreetViewLinkTests
{
    [Fact]
    public void Adresse_wird_aus_Strasse_Postleitzahl_und_Ort_gebildet() =>
        Assert.Equal(
            "Musterstraße 5, 50667 Köln",
            StreetViewLink.BuildAddress("Musterstraße 5", null, "50667", "Köln"));

    [Fact]
    public void Zusatz_steht_zwischen_Strasse_und_Ort() =>
        Assert.Equal(
            "Musterstraße 5, Gebäude B, 50667 Köln",
            StreetViewLink.BuildAddress("Musterstraße 5", "Gebäude B", "50667", "Köln"));

    [Fact]
    public void Leere_Teile_fallen_weg() =>
        Assert.Equal("Hauptstraße 1, Köln", StreetViewLink.BuildAddress(" Hauptstraße 1 ", "  ", "", " Köln "));

    [Fact]
    public void Ohne_Angaben_bleibt_die_Adresse_leer() =>
        Assert.Equal(string.Empty, StreetViewLink.BuildAddress(null, null, null, null));

    [Fact]
    public void Kartenadresse_ist_kodiert_und_zeigt_auf_die_strassenansicht()
    {
        var url = StreetViewLink.BuildUrl("Musterstraße 5, 50667 Köln");

        Assert.StartsWith("https://www.google.com/maps?q=", url, StringComparison.Ordinal);
        Assert.Contains("layer=c", url, StringComparison.Ordinal);
        Assert.Contains("%C3%9F", url, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Ohne_Adresse_gibt_es_keine_Kartenadresse() =>
        Assert.Throws<ValidationException>(() => StreetViewLink.BuildUrl("   "));
}
