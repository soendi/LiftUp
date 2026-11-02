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
    /// Kartenadresse zur Straßenansicht: Google sucht die Adresse und wechselt auf das
    /// Straßenbild an dieser Stelle (<c>layer=c</c>).
    /// </summary>
    public static string BuildUrl(string address)
    {
        var query = address.Trim();
        if (query.Length == 0)
        {
            throw new ValidationException("Für die Straßenansicht fehlt die Adresse des Projekts.");
        }

        return "https://www.google.com/maps?q=" + Uri.EscapeDataString(query) + "&layer=c&hl=de";
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
