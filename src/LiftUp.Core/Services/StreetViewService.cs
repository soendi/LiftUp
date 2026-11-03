using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Services;

/// <summary>Punkt auf der Erde in Grad.</summary>
public sealed record GeoPoint(double Latitude, double Longitude);

/// <summary>
/// Ziel der Strassenansicht: der Punkt auf der Strasse, die Blickrichtung zum Gebäude
/// (Grad im Uhrzeigersinn ab Nord) und der Name der Strasse, wenn OpenStreetMap ihn kennt.
/// </summary>
public sealed record StreetViewTarget(GeoPoint Viewpoint, double? Heading, string? RoadName, string? PanoId = null);

/// <summary>Amtliche Aufnahme von Google: Ort der Kamera und ihre Kennung.</summary>
public sealed record StreetViewPano(GeoPoint Location, string PanoId);

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
    /// Ist ein Google-Schlüssel hinterlegt, wird zusätzlich die amtliche Aufnahme an dieser
    /// Stelle abgefragt – <c>source=google</c> schliesst privat hochgeladene Panoramen aus.
    /// </summary>
    public async Task<StreetViewTarget?> LocateAsync(
        string address, string? apiKey, CancellationToken cancellationToken)
    {
        var building = await FindAsync(address, cancellationToken);
        if (building is null)
        {
            return null;
        }

        var roads = await FindRoadsAsync(building, cancellationToken);
        var road = StreetViewGeometry.NearestRoad(building, roads);
        var viewpoint = road?.Point ?? building;

        var pano = await FindOfficialPanoAsync(viewpoint, apiKey, cancellationToken);
        if (pano is not null)
        {
            // Die Kamera steht dort, wo Google fotografiert hat; geschaut wird zum Gebäude.
            var panoHeading = StreetViewGeometry.BearingDegrees(pano.Location, building);
            return new StreetViewTarget(pano.Location, panoHeading, road?.Name, pano.PanoId);
        }

        var heading = road is null ? (double?)null : StreetViewGeometry.BearingDegrees(road.Point, building);
        return new StreetViewTarget(viewpoint, heading, road?.Name);
    }

    /// <summary>
    /// Adresse der Metadaten-Abfrage. <c>source=google</c> begrenzt die Suche auf amtliche
    /// Aufnahmen; <c>radius=50</c> ist das Maximum, das Google zulässt.
    /// </summary>
    public static string BuildMetadataUrl(GeoPoint point, string apiKey)
    {
        if (point.Latitude is < -90 or > 90 || point.Longitude is < -180 or > 180)
        {
            throw new ValidationException("Die gefundenen Koordinaten liegen nicht auf der Erde.");
        }

        return "https://maps.googleapis.com/maps/api/streetview/metadata?location="
               + point.Latitude.ToString("0.######", CultureInfo.InvariantCulture)
               + ","
               + point.Longitude.ToString("0.######", CultureInfo.InvariantCulture)
               + "&radius=50&source=google&key="
               + Uri.EscapeDataString(apiKey.Trim());
    }

    /// <summary>
    /// Fragt bei Google nach der amtlichen Aufnahme an dieser Stelle. Ohne Schlüssel oder ohne
    /// Treffer bleibt es bei <c>null</c>. Metadaten-Abfragen kosten bei Google nichts.
    /// </summary>
    private async Task<StreetViewPano?> FindOfficialPanoAsync(
        GeoPoint point, string? apiKey, CancellationToken cancellationToken)
    {
        var key = apiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            return null;
        }

        try
        {
            using var response = await Client.GetAsync(BuildMetadataUrl(point, key), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParsePano(json);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Liest Status, Ort und Kennung des Panoramas; ohne amtliche Aufnahme <c>null</c>.</summary>
    public static StreetViewPano? ParsePano(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject result)
            {
                return null;
            }

            if (!string.Equals((string?)result["status"], "OK", StringComparison.Ordinal))
            {
                return null;
            }

            if (result["location"] is not JsonObject location)
            {
                return null;
            }

            var panoId = (string?)result["pano_id"];
            if (string.IsNullOrWhiteSpace(panoId))
            {
                return null;
            }

            var latitude = ReadCoordinate(location["lat"]);
            var longitude = ReadCoordinate(location["lng"]);
            if (latitude is null || longitude is null)
            {
                return null;
            }

            return new StreetViewPano(new GeoPoint(latitude.Value, longitude.Value), panoId);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static double? ReadCoordinate(JsonNode? node)
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
