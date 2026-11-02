using System.Globalization;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Antwort der Adresssuche (Nominatim) auswerten – ohne Netzzugriff.</summary>
public class StreetViewServiceTests
{
    private const string Sample = """
        [
          {
            "place_id": 12345,
            "lat": "47.408333",
            "lon": "9.6",
            "display_name": "Hauptstrasse 56, 9436 Balgach, Schweiz"
          }
        ]
        """;

    [Fact]
    public void Erster_Treffer_liefert_die_Koordinaten()
    {
        var point = StreetViewService.ParseCoordinates(Sample);

        Assert.NotNull(point);
        Assert.Equal(47.408333, point!.Latitude, 6);
        Assert.Equal(9.6, point.Longitude, 6);
    }

    [Fact]
    public void Ohne_Treffer_gibt_es_keine_Koordinaten()
    {
        Assert.Null(StreetViewService.ParseCoordinates("[]"));
        Assert.Null(StreetViewService.ParseCoordinates(""));
        Assert.Null(StreetViewService.ParseCoordinates("kein JSON"));
    }

    [Fact]
    public void Unvollstaendiger_Treffer_zaehlt_nicht() =>
        Assert.Null(StreetViewService.ParseCoordinates("[{\"lat\":\"47.4\"}]"));
}
