using LiftUp.Core.Data;
using LiftUp.Core.Sync;

namespace LiftUp.Core.Services;

/// <summary>
/// Google-Maps-Schlüssel für die Strassenansicht. Er liegt geschützt in der Datenbank, wie der
/// KI-Schlüssel. Ohne ihn löst Google die Adresse selbst auf – und zeigt dann unter Umständen ein
/// privat hochgeladenes Panorama statt der amtlichen Strassenaufnahme.
/// </summary>
public static class StreetViewSettings
{
    private const string ApiKeySetting = "StreetView.ApiKey";

    /// <summary>Der hinterlegte Schlüssel; leer, wenn keiner eingetragen ist.</summary>
    public static string LoadApiKey(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return LocalSecretProtector.Unprotect(database.GetSetting(ApiKeySetting));
    }

    /// <summary>Merkt sich den Schlüssel geschützt; ein leerer Wert entfernt den bisherigen.</summary>
    public static void SaveApiKey(Database database, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(database);

        var text = (apiKey ?? string.Empty).Trim();
        database.SetSetting(
            ApiKeySetting,
            text.Length == 0 ? string.Empty : LocalSecretProtector.Protect(text));
    }
}
