using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Amtliche Aufnahme von Google: Antwort der Metadaten-Abfrage und die daraus gebaute Adresse.
/// Ohne Netzzugriff geprüft, mit einer Antwort im dokumentierten Aufbau.
/// </summary>
public class StreetViewPanoTests
{
    private const string Found = """
        {
          "copyright": "© 2017 Google",
          "date": "2016-05",
          "location": { "lat": 48.85783227207914, "lng": 2.295226175151347 },
          "pano_id": "tu510ie_z4ptBZYo2BGEJg",
          "status": "OK"
        }
        """;

    [Fact]
    public void Amtliche_aufnahme_wird_gelesen()
    {
        var pano = StreetViewService.ParsePano(Found);

        Assert.NotNull(pano);
        Assert.Equal("tu510ie_z4ptBZYo2BGEJg", pano!.PanoId);
        Assert.Equal(48.857832, pano.Location.Latitude, 6);
        Assert.Equal(2.295226, pano.Location.Longitude, 6);
    }

    [Fact]
    public void Ohne_treffer_gibt_es_keine_aufnahme()
    {
        Assert.Null(StreetViewService.ParsePano("{\"status\":\"ZERO_RESULTS\"}"));
        Assert.Null(StreetViewService.ParsePano("{\"status\":\"REQUEST_DENIED\"}"));
        Assert.Null(StreetViewService.ParsePano("{}"));
        Assert.Null(StreetViewService.ParsePano("kein JSON"));
    }

    [Fact]
    public void Unvollstaendige_antwort_zaehlt_nicht()
    {
        Assert.Null(StreetViewService.ParsePano("{\"status\":\"OK\",\"pano_id\":\"abc\"}"));
        Assert.Null(StreetViewService.ParsePano("{\"status\":\"OK\",\"location\":{\"lat\":1,\"lng\":2}}"));
    }

    [Fact]
    public void Metadaten_abfrage_laesst_nur_amtliche_aufnahmen_zu()
    {
        var url = StreetViewService.BuildMetadataUrl(new GeoPoint(47.4061586, 9.602873), " ABC-123 ");

        Assert.Contains("location=47.406159,9.602873", url, StringComparison.Ordinal);
        Assert.Contains("radius=50", url, StringComparison.Ordinal);
        Assert.Contains("source=google", url, StringComparison.Ordinal);
        Assert.Contains("key=ABC-123", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Kamera_schaut_leicht_nach_oben()
    {
        var url = StreetViewLink.BuildPanoUrl(
            48.857832, 2.295226, 270, null, StreetViewLink.DefaultPitch);

        Assert.Contains("&pitch=10", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Neigung_bleibt_im_erlaubten_bereich()
    {
        Assert.Contains("&pitch=90", StreetViewLink.BuildPanoUrl(48.857832, 2.295226, null, null, 140),
            StringComparison.Ordinal);
        Assert.Contains("&pitch=-90", StreetViewLink.BuildPanoUrl(48.857832, 2.295226, null, null, -120),
            StringComparison.Ordinal);
        Assert.DoesNotContain("&pitch=", StreetViewLink.BuildPanoUrl(48.857832, 2.295226, null, null, null),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Kartenadresse_nennt_die_aufnahme()
    {
        var url = StreetViewLink.BuildPanoUrl(48.857832, 2.295226, 270, "tu510ie_z4ptBZYo2BGEJg");

        Assert.Contains("&pano=tu510ie_z4ptBZYo2BGEJg", url, StringComparison.Ordinal);
        Assert.Contains("&heading=270", url, StringComparison.Ordinal);
    }
}
