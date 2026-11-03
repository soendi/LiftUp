using System.Globalization;

namespace LiftUp.Core.Domain;

/// <summary>
/// Adresse für Google Street View: aus den Adressteilen des Projekts wird eine Suchadresse
/// und daraus die Kartenadresse, die direkt die Straßenansicht öffnet.
/// </summary>
public static class StreetViewLink
{
    /// <summary>
    /// Neigung der Kamera in Grad: leicht nach oben, damit vom Gebäude mehr zu sehen ist als
    /// Strasse und Trottoir. Google erlaubt Werte von -90 bis 90.
    /// </summary>
    public const double DefaultPitch = 10;
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
        BuildPanoUrl(latitude, longitude, null, null);

    /// <summary>
    /// Kartenadresse mit Blickrichtung. <paramref name="heading"/> dreht die Kamera zum Gebäude,
    /// damit nicht die Strasse entlang geschaut wird; die Gradzahl zählt im Uhrzeigersinn ab Nord.
    /// </summary>
    public static string BuildPanoUrl(double latitude, double longitude, double? heading) =>
        BuildPanoUrl(latitude, longitude, heading, null, null);

    /// <summary>
    /// Kartenadresse mit Blickrichtung und festgelegter Aufnahme. Mit <paramref name="panoId"/> zeigt
    /// Google genau diese Aufnahme statt der nächstgelegenen – damit erscheint kein privat
    /// hochgeladenes Panorama.
    /// </summary>
    public static string BuildPanoUrl(double latitude, double longitude, double? heading, string? panoId) =>
        BuildPanoUrl(latitude, longitude, heading, panoId, null);

    /// <summary>
    /// Kartenadresse mit Blickrichtung, Aufnahme und Neigung. <paramref name="pitch"/> kippt die
    /// Kamera nach oben (positiv) oder unten (negativ); Werte ausserhalb von -90 bis 90 werden
    /// auf den erlaubten Bereich begrenzt.
    /// </summary>
    public static string BuildPanoUrl(
        double latitude, double longitude, double? heading, string? panoId, double? pitch)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new ValidationException("Die gefundenen Koordinaten liegen nicht auf der Erde.");
        }

        var url = "https://www.google.com/maps/@?api=1&map_action=pano&viewpoint="
                  + latitude.ToString("0.######", CultureInfo.InvariantCulture)
                  + ","
                  + longitude.ToString("0.######", CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(panoId))
        {
            url += "&pano=" + Uri.EscapeDataString(panoId.Trim());
        }

        if (heading is { } value)
        {
            var normalized = ((value % 360) + 360) % 360;
            url += "&heading=" + Math.Round(normalized).ToString("0", CultureInfo.InvariantCulture);
        }

        if (pitch is { } angle)
        {
            var normalizedPitch = Math.Clamp(angle, -90, 90);
            url += "&pitch=" + Math.Round(normalizedPitch).ToString("0", CultureInfo.InvariantCulture);
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
