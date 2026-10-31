using System.Text.Json;

namespace LiftUp.Core.Documents;

/// <summary>
/// WebView2 stellt Nachrichten aus dem Editor als JSON bereit. Wird eine Zeichenkette
/// gesendet, liegt sie als JSON-String vor (<c>"{\"type\":…}"</c>); erst der innere
/// Text ist das eigentliche JSON. Ohne dieses Entpacken bleibt jede Nachricht
/// unbeantwortet, weil am String kein Feld gelesen werden kann.
/// </summary>
public static class WebMessagePayload
{
    /// <summary>Liefert das Nutz-JSON einer WebView2-Nachricht oder <c>null</c>.</summary>
    public static string? Unwrap(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed[0] != '"')
        {
            return trimmed;
        }

        var inner = JsonSerializer.Deserialize<string>(trimmed);
        return string.IsNullOrWhiteSpace(inner) ? null : inner;
    }
}
