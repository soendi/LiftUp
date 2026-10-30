using System.Net.Sockets;
using LiftUp.Core.Data;

namespace LiftUp.Core.Diagnostics;

public sealed record LocalDatabaseStatus(
    string Path,
    bool FileExistedBefore,
    bool Ready,
    int SchemaVersion,
    long SizeBytes,
    string Message);

public sealed record ServerDatabaseStatus(string Host, int Port, bool Reachable, string Message);

public sealed record ServerSettings(string Host, int Port)
{
    public const string HostVariable = "LIFTUP_PG_HOST";
    public const string PortVariable = "LIFTUP_PG_PORT";
    public const int DefaultPort = 5432;

    public static ServerSettings FromEnvironment()
    {
        var host = Environment.GetEnvironmentVariable(HostVariable);
        if (string.IsNullOrWhiteSpace(host))
        {
            host = "localhost";
        }

        var portText = Environment.GetEnvironmentVariable(PortVariable);
        var port = int.TryParse(portText, out var parsed) && parsed is > 0 and < 65536 ? parsed : DefaultPort;
        return new ServerSettings(host.Trim(), port);
    }
}

/// <summary>
/// Prüft die beiden Datenbanken: die lokale SQLite-Datei (wird bei Bedarf angelegt)
/// und die Erreichbarkeit der späteren PostgreSQL-Datenbank.
/// </summary>
public static class DatabaseDiagnostics
{
    /// <summary>
    /// Prüft die lokale Datenbank und legt Datei samt Schema an, falls sie fehlt.
    /// Damit ist "nachholen" abgedeckt: beim ersten Aufruf entsteht die Datenbank,
    /// beim zweiten wird nur noch geprüft.
    /// </summary>
    public static LocalDatabaseStatus CheckLocalDatabase(string? filePath = null)
    {
        string path;
        try
        {
            path = Path.GetFullPath(filePath ?? AppPaths.GetDatabasePath());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new LocalDatabaseStatus(filePath ?? string.Empty, false, false, 0, 0,
                "Der Pfad der lokalen Datenbank ist ungültig.");
        }

        var existed = File.Exists(path);

        try
        {
            var database = new Database(path);
            database.EnsureCreated();
            var version = database.GetSchemaVersion();
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;

            return new LocalDatabaseStatus(
                path,
                existed,
                true,
                version,
                size,
                existed
                    ? $"Lokale Datenbank vorhanden (Schema {version})."
                    : $"Lokale Datenbank angelegt (Schema {version}).");
        }
        catch (Exception exception)
        {
            return new LocalDatabaseStatus(path, existed, false, 0, 0,
                $"Lokale Datenbank nicht nutzbar: {exception.Message}");
        }
    }

    /// <summary>
    /// Prüft per TCP, ob ein PostgreSQL-Server antwortet – bewusst ohne zusätzliche
    /// Bibliothek, damit die Installationsprüfung schlank bleibt.
    /// </summary>
    public static async Task<ServerDatabaseStatus> CheckServerDatabaseAsync(
        string? host = null,
        int? port = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var settings = ServerSettings.FromEnvironment();
        host = string.IsNullOrWhiteSpace(host) ? settings.Host : host.Trim();
        port ??= settings.Port;
        var limit = timeout ?? TimeSpan.FromSeconds(2);

        using var client = new TcpClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(limit);

        try
        {
            await client.ConnectAsync(host!, port.Value, linked.Token);
            return new ServerDatabaseStatus(host!, port.Value, true,
                $"Server-Datenbank erreichbar: {host}:{port}.");
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException)
        {
            return new ServerDatabaseStatus(host!, port.Value, false,
                $"Server-Datenbank nicht erreichbar ({host}:{port}). Später über {ServerSettings.HostVariable} und {ServerSettings.PortVariable} einstellbar.");
        }
    }
}
