using System.Text;
using System.Text.Json;
using LiftUp.Core.Diagnostics;

namespace LiftUp.Preflight;

/// <summary>
/// Prüft die Voraussetzungen von LiftUp, ohne die Oberfläche zu starten.
/// Wird vom Setup und vom Support genutzt.
///
/// Rückgabewerte:
///   0 = alles bereit
///   1 = lokale Datenbank nicht nutzbar
///   2 = nur die Server-Datenbank fehlt (kein Fehler der Anwendung)
///
/// Aufruf:
///   LiftUp.Preflight.exe [--db Pfad] [--server host:port] [--json]
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // Ohne Konsole (z. B. im Setup) nicht kritisch.
        }

        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var databasePath = ReadOption(args, "--db");
        var server = ReadOption(args, "--server");

        string? host = null;
        int? port = null;
        if (!string.IsNullOrWhiteSpace(server))
        {
            var parts = server.Split(':', 2, StringSplitOptions.TrimEntries);
            host = parts[0];
            if (parts.Length == 2 && int.TryParse(parts[1], out var parsedPort))
            {
                port = parsedPort;
            }
        }

        var local = DatabaseDiagnostics.CheckLocalDatabase(databasePath);
        var remote = await DatabaseDiagnostics.CheckServerDatabaseAsync(host, port);

        if (json)
        {
            var payload = new
            {
                local = new
                {
                    local.Path,
                    local.FileExistedBefore,
                    local.Ready,
                    local.SchemaVersion,
                    local.SizeBytes,
                    local.Message,
                },
                server = new { remote.Host, remote.Port, remote.Reachable, remote.Message },
                runtime = Environment.Version.ToString(),
            };

            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine("LiftUp – Voraussetzungen");
            Console.WriteLine($"  .NET:               {Environment.Version}");
            Console.WriteLine($"  Lokale Datenbank:   {(local.Ready ? "bereit" : "FEHLER")} – {local.Message}");
            if (local.Ready)
            {
                Console.WriteLine($"                      {local.Path} ({local.SizeBytes} Bytes, Schema {local.SchemaVersion})");
            }

            Console.WriteLine($"  Server-Datenbank:   {(remote.Reachable ? "erreichbar" : "nicht erreichbar")} – {remote.Message}");
        }

        if (!local.Ready)
        {
            return 1;
        }

        return remote.Reachable ? 0 : 2;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
