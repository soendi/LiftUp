using System.Globalization;
using System.Text;

namespace LiftUp.Core.Domain;

/// <summary>
/// Preise aus Textfeldern lesen: „5000“, „5000.50“, „5'000.50“, „1.250,50“ und
/// „CHF 3000.–“ sind alle dieselbe Zahl. Geschrieben wird immer mit Punkt und zwei
/// Stellen, damit die Summe in der Spalte „Kosten“ überall gleich aussieht.
/// </summary>
public static class PriceText
{
    /// <summary>
    /// Liest einen Preis. Buchstaben, Währungen und Leerzeichen stören nicht; was keine
    /// Zahl ergibt, zählt als null.
    /// </summary>
    public static decimal Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0m;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsDigit(character) || character is '.' or ',' or '\'' or '’' or ' ' or '\u00a0' or '\u202f')
            {
                builder.Append(character);
                continue;
            }

            // Ein Minus zählt nur am Anfang: „-250“ ist negativ, „5000.-“ bleibt positiv.
            if (character == '-' && builder.Length == 0)
            {
                builder.Append(character);
            }
        }

        var text = builder.ToString()
            .Replace("'", string.Empty)
            .Replace("’", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("\u00a0", string.Empty)
            .Replace("\u202f", string.Empty)
            .Trim('.', ',');

        if (text.Length == 0 || text == "-")
        {
            return 0m;
        }

        return decimal.TryParse(Normalize(text), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    /// <summary>Preis mit Punkt und zwei Stellen, etwa „5000.00“.</summary>
    public static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Ein einzelnes Trennzeichen mit ein bis zwei Ziffern dahinter ist das Dezimalzeichen
    /// („5000.5“, „1250,50“); drei Ziffern oder mehrere Trennzeichen gehören zu den Tausendern
    /// („5.000“, „1.250.000“). So wird „5000“ genauso erkannt wie „5'000“.
    /// </summary>
    private static string Normalize(string text)
    {
        var commas = text.Count(character => character == ',');
        var dots = text.Count(character => character == '.');

        if (commas > 0 && dots > 0)
        {
            // Der letzte Trenner ist das Dezimalzeichen, der andere gehört zu den Tausendern.
            return text.LastIndexOf(',') > text.LastIndexOf('.')
                ? text.Replace(".", string.Empty).Replace(',', '.')
                : text.Replace(",", string.Empty);
        }

        if (commas == 0 && dots == 0)
        {
            return text;
        }

        var separator = commas > 0 ? ',' : '.';
        var count = commas + dots;
        var digitsAfterLast = text.Length - text.LastIndexOf(separator) - 1;

        return count == 1 && digitsAfterLast <= 2
            ? text.Replace(separator, '.')
            : text.Replace(separator.ToString(), string.Empty);
    }
}
