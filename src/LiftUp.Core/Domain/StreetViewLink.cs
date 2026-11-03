using System.Globalization;

namespace LiftUp.Core.Domain;

/// <summary>
/// Adresse für Google Street View: aus den Adressteilen des Projekts wird eine Suchadresse
/// und daraus die Kartenadresse, die direkt die Straßenansicht öffnet.
/// </summary>
public static class StreetViewLink
{
    /// <summary>Setzt Straße, Zusatz, PLZ und Ort zu einer Adresse zusammen; leere Teile fallen weg.</summary>
    public static string BuildAddress(string? street, string? line2, string? zip, string? city)
    {
        var parts = new List<string>();

        AddPart(parts, street);
        AddPart(parts, line2);

        var zipText = zip?.Trim() ?? string.Empty;
        var cityText = city?.Trim() ?? string.Empty;
        var place = string.Join(' ', new[] { zipText, cityText }.Where(part => part.Length > 0));
        if (place.Length > 0)
        {
            parts.Add(place);
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Kartenadresse zur Straßenansicht: Google sucht die Adresse und springt mit
    /// <c>map_action=pano</c> auf das Straßenbild an dieser Stelle. Die Adressteile sind mit
    /// „+“ verbunden (z. B. <c>Hauptstrasse+56+9436+Balgach</c>), Kommas entfallen.
    /// </summary>
    public static string BuildUrl(string address)
    {
        var query = address.Trim();
        if (query.Length == 0)
        {
            throw new ValidationException("Für die Straßenansicht fehlt die Adresse des Projekts.");
        }

        var parts = query
            .Replace(",", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);

        return "https://www.google.com/maps/search/?api=1&query="
               + string.Join('+', parts)
               + "&map_action=pano";
    }

    /// <summary>
    /// Kartenadresse, die direkt die Straßenansicht zeigt. <c>map_action=pano</c> greift bei
    /// Google nur zusammen mit einem Punkt – ohne <c>viewpoint</c> erscheint die Karte.
    /// </summary>
    public static string BuildPanoUrl(double latitude, double longitude) =>
        BuildPanoUrl(latitude, longitude, null);

    /// <summary>
    /// Kartenadresse mit Blickrichtung. <paramref name="heading"/> dreht die Kamera zum Gebäude,
    /// damit nicht die Strasse entlang geschaut wird; die Gradzahl zählt im Uhrzeigersinn ab Nord.
    /// </summary>
    public static string BuildPanoUrl(double latitude, double longitude, double? heading)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new ValidationException("Die gefundenen Koordinaten liegen nicht auf der Erde.");
        }

        var url = "https://www.google.com/maps/@?api=1&map_action=pano&viewpoint="
                  + latitude.ToString("0.######", CultureInfo.InvariantCulture)
                  + ","
                  + longitude.ToString("0.######", CultureInfo.InvariantCulture);

        if (heading is { } value)
        {
            var normalized = ((value % 360) + 360) % 360;
            url += "&heading=" + Math.Round(normalized).ToString("0", CultureInfo.InvariantCulture);
        }

        return url + "&hl=de";
    }

    private static void AddPart(List<string> parts, string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length > 0)
        {
            parts.Add(text);
        }
    }
}
