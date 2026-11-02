using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace LiftUp.Core.Services;

/// <summary>Punkt auf der Erde in Grad.</summary>
public sealed record GeoPoint(double Latitude, double Longitude);

/// <summary>
/// Sucht zu einer Adresse die Koordinaten, damit Google Street View direkt das Straßenbild
/// öffnet – <c>map_action=pano</c> greift dort nur mit einem Punkt. Gefragt wird bei
/// OpenStreetMap (Nominatim), weil das ohne Schlüssel und ohne Konto geht. Ohne Treffer
/// bleibt es bei der Kartensuche, die Google aus der Adresse selbst auflöst.
/// </summary>
public sealed class StreetViewService
{
    private const string SearchUrl = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&q=";

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
