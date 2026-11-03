using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace LiftUp.Core.Services;

/// <summary>Punkt auf der Erde in Grad.</summary>
public sealed record GeoPoint(double Latitude, double Longitude);

/// <summary>
/// Ziel der Strassenansicht: der Punkt auf der Strasse, die Blickrichtung zum Gebäude
/// (Grad im Uhrzeigersinn ab Nord) und der Name der Strasse, wenn OpenStreetMap ihn kennt.
/// </summary>
public sealed record StreetViewTarget(GeoPoint Viewpoint, double? Heading, string? RoadName);

/// <summary>
/// Sucht zu einer Adresse die Koordinaten, damit Google Street View direkt das Straßenbild
/// öffnet – <c>map_action=pano</c> greift dort nur mit einem Punkt. Gefragt wird bei
/// OpenStreetMap (Nominatim), weil das ohne Schlüssel und ohne Konto geht. Ohne Treffer
/// bleibt es bei der Kartensuche, die Google aus der Adresse selbst auflöst.
/// </summary>
public sealed class StreetViewService
{
    private const string SearchUrl = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&q=";

    private const string OverpassUrl = "https://overpass-api.de/api/interpreter";

    // Gefragt werden die Strassen, auf denen Street View fährt; Fuss- und Dienstwege bleiben
    // aussen vor, sonst landet die Ansicht auf dem Vorplatz des Gebäudes.
    private const string RoadQuery =
        "[out:json][timeout:15];way(around:120,{0},{1})[\"highway\"~\"^(trunk|primary|secondary|tertiary|residential|unclassified|living_street)$\"];out tags geom;";

    private static readonly HttpClient Client = CreateClient();

    /// <summary>Liefert die Koordinaten der Adresse; <c>null</c>, wenn keine gefunden wurden.</summary>
    public async Task<GeoPoint?> FindAsync(string address, CancellationToken cancellationToken)
    {
        var query = address?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return null;
        }

        try
        {
            using var response = await Client.GetAsync(SearchUrl + Uri.EscapeDataString(query), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseCoordinates(json);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Kein Netz oder keine Antwort: dann eben der Kartenausschnitt.
            return null;
        }
    }

    /// <summary>
    /// Sucht zur Adresse den Punkt auf der nächsten Strasse und die Blickrichtung zum Gebäude.
    /// So öffnet Street View die Strasse vor dem Haus statt der Hausadresse selbst.
    /// </summary>
    public async Task<StreetViewTarget?> LocateAsync(string address, CancellationToken cancellationToken)
    {
        var building = await FindAsync(address, cancellationToken);
        if (building is null)
        {
            return null;
        }

        var roads = await FindRoadsAsync(building, cancellationToken);
        var road = StreetViewGeometry.NearestRoad(building, roads);
        if (road is null)
        {
            return new StreetViewTarget(building, null, null);
        }

        var heading = StreetViewGeometry.BearingDegrees(road.Point, building);
        return new StreetViewTarget(road.Point, heading, road.Name);
    }

    /// <summary>
    /// Holt die Strassen im Umkreis von 120 Metern. Scheitert die Abfrage, bleibt es beim
    /// Gebäudepunkt – die Strassenansicht funktioniert dann wie bisher.
    /// </summary>
    private async Task<IReadOnlyList<StreetRoad>> FindRoadsAsync(GeoPoint point, CancellationToken cancellationToken)
    {
        try
        {
            var query = string.Format(
                CultureInfo.InvariantCulture,
                RoadQuery,
                point.Latitude.ToString("0.######", CultureInfo.InvariantCulture),
                point.Longitude.ToString("0.######", CultureInfo.InvariantCulture));

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["data"] = query,
            });

            using var response = await Client.PostAsync(OverpassUrl, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<StreetRoad>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return StreetViewGeometry.ParseRoads(json);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Kein Netz oder keine Antwort: dann der Gebäudepunkt.
            return Array.Empty<StreetRoad>();
        }
    }

    /// <summary>Liest den ersten Treffer der Antwort; ohne Treffer <c>null</c>.</summary>
    public static GeoPoint? ParseCoordinates(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray { Count: > 0 } results ||
                results[0] is not JsonObject first)
            {
                return null;
            }

            var latitude = (string?)first["lat"];
            var longitude = (string?)first["lon"];
            if (!double.TryParse(latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                return null;
            }

            return new GeoPoint(lat, lon);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // Nominatim verlangt einen erkennbaren Aufrufer.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiftUp/1.0 (Aufzugsberichte)");
        return client;
    }
}
