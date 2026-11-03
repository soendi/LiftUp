using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Auswahl der Strasse vor dem Gebäude – ohne Netzzugriff, mit einer Antwort von Overpass
/// im selben Aufbau, wie sie die Abfrage liefert.
/// </summary>
public class StreetViewGeometryTests
{
    private const string Overpass = """
        {
          "elements": [
            {
              "type": "way",
              "id": 18456009,
              "tags": { "highway": "primary", "name": "Hauptstrasse" },
              "geometry": [
                { "lat": 47.4050, "lon": 9.60300 },
                { "lat": 47.4075, "lon": 9.60300 }
              ]
            },
            {
              "type": "way",
              "id": 32674005,
              "tags": { "highway": "residential", "name": "Nebenweg" },
              "geometry": [
                { "lat": 47.4050, "lon": 9.60290 },
                { "lat": 47.4075, "lon": 9.60290 }
              ]
            }
          ]
        }
        """;

    private static readonly GeoPoint Building = new(47.4061586, 9.6027695);

    [Fact]
    public void Strassen_werden_gelesen()
    {
        var roads = StreetViewGeometry.ParseRoads(Overpass);

        Assert.Equal(2, roads.Count);
        Assert.Equal("Hauptstrasse", roads[0].Name);
        Assert.Equal("primary", roads[0].Highway);
        Assert.Equal(2, roads[0].Points.Count);
    }

    [Fact]
    public void Hauptstrasse_gewinnt_gegen_die_naehere_quartierstrasse()
    {
        var road = StreetViewGeometry.NearestRoad(Building, StreetViewGeometry.ParseRoads(Overpass));

        Assert.NotNull(road);
        Assert.Equal("Hauptstrasse", road!.Name);

        // Der Fusspunkt liegt auf der Strasse, nicht im Gebäude.
        Assert.Equal(9.603, road.Point.Longitude, 5);
        Assert.Equal(Building.Latitude, road.Point.Latitude, 4);
        Assert.InRange(road.DistanceMeters, 10, 25);
    }

    [Fact]
    public void Ohne_hauptstrasse_gewinnt_die_naechste_strasse()
    {
        const string onlySide = """
            {"elements":[{"type":"way","tags":{"highway":"residential","name":"Nebenweg"},
            "geometry":[{"lat":47.4050,"lon":9.60290},{"lat":47.4075,"lon":9.60290}]}]}
            """;

        var road = StreetViewGeometry.NearestRoad(Building, StreetViewGeometry.ParseRoads(onlySide));

        Assert.NotNull(road);
        Assert.Equal("Nebenweg", road!.Name);
        Assert.InRange(road.DistanceMeters, 5, 15);
    }

    [Fact]
    public void Blickrichtung_zeigt_vom_strassenpunkt_zum_gebaeude()
    {
        var road = StreetViewGeometry.NearestRoad(Building, StreetViewGeometry.ParseRoads(Overpass));
        Assert.NotNull(road);

        var heading = StreetViewGeometry.BearingDegrees(road!.Point, Building);

        // Die Strasse liegt östlich des Gebäudes, geschaut wird nach Westen.
        Assert.InRange(heading, 265, 275);
    }

    [Fact]
    public void Abstand_stimmt_mit_der_geographie_ueberein()
    {
        // Rund 75 Meter nach Norden.
        var distance = StreetViewGeometry.DistanceMeters(Building, new GeoPoint(47.4068346, 9.6027695));

        Assert.InRange(distance, 70, 80);
    }

    [Fact]
    public void Unbrauchbare_antwort_liefert_keine_strassen()
    {
        Assert.Empty(StreetViewGeometry.ParseRoads("kein JSON"));
        Assert.Empty(StreetViewGeometry.ParseRoads("{}"));
        Assert.Empty(StreetViewGeometry.ParseRoads(""));
        Assert.Null(StreetViewGeometry.NearestRoad(Building, Array.Empty<StreetRoad>()));
    }

    [Fact]
    public void Blickrichtung_kommt_in_die_kartenadresse()
    {
        var url = StreetViewLink.BuildPanoUrl(47.4061586, 9.603, 270.4);

        Assert.Contains("viewpoint=47.406159,9.603", url, StringComparison.Ordinal);
        Assert.Contains("&heading=270", url, StringComparison.Ordinal);
    }
}
