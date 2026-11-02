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
    public void Kartenadresse_nutzt_die_suche_mit_stra_enansicht() =>
        Assert.Equal(
            "https://www.google.com/maps/search/?api=1&query=Hauptstrasse+56+9436+Balgach&map_action=pano",
            StreetViewLink.BuildUrl("Hauptstrasse 56, 9436 Balgach"));

    [Fact]
    public void Kartenadresse_ist_kodiert_und_ohne_Komma()
    {
        var url = StreetViewLink.BuildUrl("Musterstraße 5, 50667 Köln");

        Assert.StartsWith("https://www.google.com/maps/search/?api=1&query=", url, StringComparison.Ordinal);
        Assert.EndsWith("&map_action=pano", url, StringComparison.Ordinal);
        Assert.Contains("%C3%9F", url, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", url, StringComparison.Ordinal);
        Assert.DoesNotContain(",", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Ohne_Adresse_gibt_es_keine_Kartenadresse() =>
        Assert.Throws<ValidationException>(() => StreetViewLink.BuildUrl("   "));

    [Fact]
    public void Mit_Koordinaten_wird_die_stra_enansicht_direkt_geoeffnet() =>
        Assert.Equal(
            "https://www.google.com/maps/@?api=1&map_action=pano&viewpoint=47.408333,9.6&hl=de",
            StreetViewLink.BuildPanoUrl(47.408333, 9.6));

    [Fact]
    public void Koordinaten_stehen_mit_Punkt_und_ohne_Rundungsreste() =>
        Assert.Equal(
            "https://www.google.com/maps/@?api=1&map_action=pano&viewpoint=47.5,9.25&hl=de",
            StreetViewLink.BuildPanoUrl(47.5000, 9.2500));

    [Fact]
    public void Punkte_ausserhalb_der_Erde_werden_abgelehnt() =>
        Assert.Throws<ValidationException>(() => StreetViewLink.BuildPanoUrl(91, 9.6));
}
