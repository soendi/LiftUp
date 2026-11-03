using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiftUp.Core.Services;

/// <summary>Eine Strasse aus OpenStreetMap samt ihrem Verlauf.</summary>
public sealed record StreetRoad(string? Highway, string? Name, IReadOnlyList<GeoPoint> Points);

/// <summary>Der Punkt auf einer Strasse, der einem Gebäude am nächsten liegt.</summary>
public sealed record StreetRoadPoint(GeoPoint Point, string? Name, string? Highway, double DistanceMeters);

/// <summary>
/// Rechnet mit Koordinaten, damit die Strassenansicht von der Strasse vor dem Haus öffnet und
/// nicht mitten im Gebäude: Abstand, Fusspunkt auf dem Strassenverlauf und Blickrichtung zum
/// Gebäude. Alles rein rechnerisch und ohne Netzzugriff, deshalb prüfbar.
/// </summary>
public static class StreetViewGeometry
{
    private const double EarthRadiusMeters = 6371001;

    /// <summary>
    /// Rang einer Strasse: kleinere Zahl heisst grössere Verkehrsstrasse. Der Rang geht mit
    /// einem Zuschlag in Metern in die Auswahl ein, damit die Strasse direkt vor dem Haus
    /// nicht gegen eine Hauptachse weiter weg verliert.
    /// </summary>
    private const double RankPenaltyMeters = 15;

    /// <summary>
    /// Liest die Fahrstrassen aus dem Ausschnitt der OSM-Schnittstelle (<c>map.json</c>): die
    /// Knoten liefern die Koordinaten, die Wege verweisen über ihre Kennungen darauf.
    /// </summary>
    public static IReadOnlyList<StreetRoad> ParseMapRoads(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["elements"] is not JsonArray elements)
            {
                return Array.Empty<StreetRoad>();
            }

            var nodes = new Dictionary<long, GeoPoint>();
            foreach (var element in elements)
            {
                if (element is not JsonObject node || (string?)node["type"] != "node")
                {
                    continue;
                }

                if (!TryReadId(node["id"], out var id))
                {
                    continue;
                }

                var latitude = ReadDouble(node["lat"]);
                var longitude = ReadDouble(node["lon"]);
                if (latitude is null || longitude is null)
                {
                    continue;
                }

                nodes[id] = new GeoPoint(latitude.Value, longitude.Value);
            }

            var roads = new List<StreetRoad>();
            foreach (var element in elements)
            {
                if (element is not JsonObject way || (string?)way["type"] != "way")
                {
                    continue;
                }

                if (way["tags"] is not JsonObject tags)
                {
                    continue;
                }

                var highway = (string?)tags["highway"];
                if (!IsDrivable(highway) || way["nodes"] is not JsonArray references)
                {
                    continue;
                }

                var points = new List<GeoPoint>();
                foreach (var reference in references)
                {
                    if (TryReadId(reference, out var id) && nodes.TryGetValue(id, out var point))
                    {
                        points.Add(point);
                    }
                }

                if (points.Count < 2)
                {
                    continue;
                }

                roads.Add(new StreetRoad(highway, (string?)tags["name"], points));
            }

            return roads;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return Array.Empty<StreetRoad>();
        }
    }

    /// <summary>
    /// Ob Street View auf dieser Strasse fährt. Fuss-, Rad-, Reit- und Dienstwege bleiben
    /// draussen: dort steht die Kamera auf dem Vorplatz statt auf der Strasse.
    /// </summary>
    public static bool IsDrivable(string? highway) => highway is
        "trunk" or "primary" or "secondary" or "tertiary"
        or "residential" or "unclassified" or "living_street";

    private static bool TryReadId(JsonNode? node, out long id)
    {
        id = 0;
        return node is JsonValue value && value.TryGetValue(out id);
    }

    /// <summary>Abstand zweier Punkte in Metern; ebene Näherung, genau genug für wenige hundert Meter.</summary>
    public static double DistanceMeters(GeoPoint from, GeoPoint to)
    {
        var latitude = (from.Latitude + to.Latitude) / 2 * Math.PI / 180;
        var east = (to.Longitude - from.Longitude) * Math.PI / 180 * EarthRadiusMeters * Math.Cos(latitude);
        var north = (to.Latitude - from.Latitude) * Math.PI / 180 * EarthRadiusMeters;
        return Math.Sqrt(east * east + north * north);
    }

    /// <summary>
    /// Nächster Punkt auf einer der Strassen. Unter gleich nahen Strassen gewinnt die grössere
    /// Verkehrsstrasse, innerhalb von rund 15 Metern Abstand also die Hauptstrasse vor der
    /// Quartierstrasse.
    /// </summary>
    public static StreetRoadPoint? NearestRoad(GeoPoint origin, IReadOnlyList<StreetRoad> roads)
    {
        StreetRoadPoint? best = null;
        var bestScore = double.MaxValue;

        foreach (var road in roads)
        {
            if (road.Points.Count == 0)
            {
                continue;
            }

            var (point, distance) = road.Points.Count == 1
                ? (road.Points[0], DistanceMeters(origin, road.Points[0]))
                : NearestOnPolyline(origin, road.Points);

            var score = distance + Rank(road.Highway) * RankPenaltyMeters;
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            best = new StreetRoadPoint(point, road.Name, road.Highway, distance);
        }

        return best;
    }

    /// <summary>Blickrichtung von <paramref name="from"/> nach <paramref name="to"/> in Grad (Norden = 0).</summary>
    public static double BearingDegrees(GeoPoint from, GeoPoint to)
    {
        var fromLatitude = from.Latitude * Math.PI / 180;
        var toLatitude = to.Latitude * Math.PI / 180;
        var deltaLongitude = (to.Longitude - from.Longitude) * Math.PI / 180;

        var east = Math.Sin(deltaLongitude) * Math.Cos(toLatitude);
        var north = Math.Cos(fromLatitude) * Math.Sin(toLatitude)
                    - Math.Sin(fromLatitude) * Math.Cos(toLatitude) * Math.Cos(deltaLongitude);

        var degrees = Math.Atan2(east, north) * 180 / Math.PI;
        return (degrees + 360) % 360;
    }

    private static int Rank(string? highway) => highway switch
    {
        "trunk" => 1,
        "primary" => 2,
        "secondary" => 3,
        "tertiary" => 4,
        "residential" => 5,
        "unclassified" => 6,
        "living_street" => 7,
        _ => 8,
    };

    private static (GeoPoint Point, double Distance) NearestOnPolyline(GeoPoint origin, IReadOnlyList<GeoPoint> points)
    {
        var bestPoint = points[0];
        var bestDistance = double.MaxValue;

        for (var index = 1; index < points.Count; index += 1)
        {
            var (point, distance) = NearestOnSegment(origin, points[index - 1], points[index]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPoint = point;
            }
        }

        return (bestPoint, bestDistance);
    }

    /// <summary>
    /// Fusspunkt des Gebäudes auf einem Strassenstück. Gerechnet wird in Metern relativ zum
    /// Gebäude; der Fusspunkt wird an den Enden des Stücks begrenzt.
    /// </summary>
    private static (GeoPoint Point, double Distance) NearestOnSegment(GeoPoint origin, GeoPoint start, GeoPoint end)
    {
        var latitude = origin.Latitude * Math.PI / 180;
        var metersPerDegree = Math.PI / 180 * EarthRadiusMeters;
        var metersPerDegreeEast = metersPerDegree * Math.Cos(latitude);

        double East(GeoPoint point) => (point.Longitude - origin.Longitude) * metersPerDegreeEast;
        double North(GeoPoint point) => (point.Latitude - origin.Latitude) * metersPerDegree;

        var startEast = East(start);
        var startNorth = North(start);
        var deltaEast = East(end) - startEast;
        var deltaNorth = North(end) - startNorth;
        var lengthSquared = deltaEast * deltaEast + deltaNorth * deltaNorth;

        var position = lengthSquared <= 0
            ? 0
            : Math.Clamp(-(startEast * deltaEast + startNorth * deltaNorth) / lengthSquared, 0, 1);

        var east = startEast + deltaEast * position;
        var north = startNorth + deltaNorth * position;
        var distance = Math.Sqrt(east * east + north * north);

        var point = new GeoPoint(
            origin.Latitude + north / metersPerDegree,
            origin.Longitude + (Math.Abs(metersPerDegreeEast) < double.Epsilon ? 0 : east / metersPerDegreeEast));

        return (point, distance);
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<double>();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
