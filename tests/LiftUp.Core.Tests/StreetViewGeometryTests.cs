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
    private const string Roads = """
        {
          "version": "0.6",
          "elements": [
            { "type": "node", "id": 1, "lat": 47.4050, "lon": 9.60300 },
            { "type": "node", "id": 2, "lat": 47.4075, "lon": 9.60300 },
            { "type": "node", "id": 3, "lat": 47.4050, "lon": 9.60290 },
            { "type": "node", "id": 4, "lat": 47.4075, "lon": 9.60290 },
            { "type": "node", "id": 5, "lat": 47.40616, "lon": 9.60277 },
            { "type": "way", "id": 10, "nodes": [1, 2],
              "tags": { "highway": "primary", "name": "Hauptstrasse" } },
            { "type": "way", "id": 11, "nodes": [3, 4],
              "tags": { "highway": "residential", "name": "Nebenweg" } },
            { "type": "way", "id": 12, "nodes": [5, 3],
              "tags": { "highway": "footway" } },
            { "type": "way", "id": 13, "nodes": [5, 4],
              "tags": { "highway": "service" } }
          ]
        }
        """;

    private static readonly GeoPoint Building = new(47.4061586, 9.6027695);

    [Fact]
    public void Nur_fahrstrassen_werden_gelesen()
    {
        var roads = StreetViewGeometry.ParseMapRoads(Roads);

        Assert.Equal(2, roads.Count);
        Assert.Equal("Hauptstrasse", roads[0].Name);
        Assert.Equal("primary", roads[0].Highway);
        Assert.Equal(2, roads[0].Points.Count);
        Assert.Equal(new GeoPoint(47.4050, 9.60300), roads[0].Points[0]);
        Assert.DoesNotContain(roads, road => road.Highway is "footway" or "service");
    }

    [Fact]
    public void Hauptstrasse_gewinnt_gegen_die_naehere_quartierstrasse()
    {
        var road = StreetViewGeometry.NearestRoad(Building, StreetViewGeometry.ParseMapRoads(Roads));

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
            {"elements":[
              {"type":"node","id":3,"lat":47.4050,"lon":9.60290},
              {"type":"node","id":4,"lat":47.4075,"lon":9.60290},
              {"type":"way","id":11,"nodes":[3,4],"tags":{"highway":"residential","name":"Nebenweg"}}]}
            """;

        var road = StreetViewGeometry.NearestRoad(Building, StreetViewGeometry.ParseMapRoads(onlySide));

        Assert.NotNull(road);
        Assert.Equal("Nebenweg", road!.Name);
        Assert.InRange(road.DistanceMeters, 5, 15);
    }

    [Fact]
    public void Blickrichtung_zeigt_vom_strassenpunkt_zum_gebaeude()
    {
        var road = StreetViewGeometry.NearestRoad(Building, StreetViewGeometry.ParseMapRoads(Roads));
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
        Assert.Empty(StreetViewGeometry.ParseMapRoads("kein JSON"));
        Assert.Empty(StreetViewGeometry.ParseMapRoads("{}"));
        Assert.Empty(StreetViewGeometry.ParseMapRoads(""));
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
