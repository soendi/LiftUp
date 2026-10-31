using System.Text.Json;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Npgsql;

namespace LiftUp.Core.Sync;

/// <summary>Eine PostgreSQL-Hauptversion laut postgresql.org.</summary>
public sealed record PostgresRelease(int Major, string Version, bool Supported, bool Current);

/// <summary>
/// Ermittelt die aktuelle PostgreSQL-Version. Das Server-Setup lädt damit immer
/// die neueste Fassung, statt eine Version fest einzubauen.
/// </summary>
public static class PostgresReleaseService
{
    /// <summary>Offizielle Liste aller PostgreSQL-Versionen (JSON).</summary>
    public const string VersionsUrl = "https://www.postgresql.org/versions.json";

    public static IReadOnlyList<PostgresRelease> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ValidationException("Die Versionsliste von postgresql.org ist leer.");
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ValidationException("Die Versionsliste von postgresql.org hat ein unerwartetes Format.");
        }

        var releases = new List<PostgresRelease>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var major = entry.TryGetProperty("major", out var majorElement) ? majorElement.GetString() : null;
            if (major is null || !int.TryParse(major, out var number) || number < 10)
            {
                // Hauptversionen vor 10 (z. B. „9.6“) sind längst abgekündigt.
                continue;
            }

            var minor = entry.TryGetProperty("latestMinor", out var minorElement) ? minorElement.GetString() : null;
            var supported = entry.TryGetProperty("supported", out var supportedElement) &&
                            supportedElement.ValueKind == JsonValueKind.True;
            var current = entry.TryGetProperty("current", out var currentElement) &&
                          currentElement.ValueKind == JsonValueKind.True;

            releases.Add(new PostgresRelease(
                number,
                string.IsNullOrWhiteSpace(minor) ? major : $"{major}.{minor}",
                supported,
                current));
        }

        if (releases.Count == 0)
        {
            throw new ValidationException("In der Versionsliste von postgresql.org war keine Hauptversion enthalten.");
        }

        return releases;
    }

    /// <summary>Die höchste aufgeführte Hauptversion – also die neueste Fassung.</summary>
    public static PostgresRelease Latest(string json) =>
        Parse(json).OrderByDescending(release => release.Major).First();

    /// <summary>Lädt die Versionsliste und liefert die neueste Hauptversion.</summary>
    public static async Task<PostgresRelease> FetchLatestAsync(
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        using var owned = client is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) } : null;
        var http = client ?? owned!;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, VersionsUrl);
            request.Headers.UserAgent.ParseAdd("LiftUp-ServerSetup");
            var json = await http.GetStringAsync(VersionsUrl, cancellationToken).ConfigureAwait(false);
            return Latest(json);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new ValidationException(
                $"Die aktuelle PostgreSQL-Version konnte nicht abgerufen werden ({exception.Message}). " +
                "Bitte die Internetverbindung prüfen und das Setup erneut starten.");
        }
    }

    /// <summary>Passende winget-Kennung, z. B. <c>PostgreSQL.PostgreSQL.18</c>.</summary>
    public static string WingetPackageId(PostgresRelease release) =>
        $"PostgreSQL.PostgreSQL.{release.Major}";
}
