using System.Net;
using System.Net.Sockets;
using LiftUp.Core.Data;
using LiftUp.Core.Diagnostics;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class DatabaseDiagnosticsTests
{
    [Fact]
    public void Fehlende_lokale_datenbank_wird_angelegt()
    {
        var path = Path.Combine(Path.GetTempPath(), "liftup-tests", Guid.NewGuid().ToString("N"), "check.db");
        try
        {
            var first = DatabaseDiagnostics.CheckLocalDatabase(path);

            Assert.True(first.Ready);
            Assert.False(first.FileExistedBefore);
            Assert.Equal(Database.CurrentSchemaVersion, first.SchemaVersion);
            Assert.True(File.Exists(path));
            Assert.Contains("angelegt", first.Message);

            var second = DatabaseDiagnostics.CheckLocalDatabase(path);
            Assert.True(second.Ready);
            Assert.True(second.FileExistedBefore);
            Assert.Contains("vorhanden", second.Message);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var directory = Path.GetDirectoryName(path)!;
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unter Windows kann die Datei noch kurz durch den Pool belegt sein.
            }
        }
    }

    [Fact]
    public void Ungueltiger_pfad_wird_gemeldet()
    {
        var status = DatabaseDiagnostics.CheckLocalDatabase("\0ungültig");
        Assert.False(status.Ready);
    }

    [Fact]
    public async Task Erreichbarer_server_wird_erkannt()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var status = await DatabaseDiagnostics.CheckServerDatabaseAsync("127.0.0.1", port, TimeSpan.FromSeconds(2));

            Assert.True(status.Reachable);
            Assert.Equal(port, status.Port);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Fehlender_server_wird_erkannt()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var status = await DatabaseDiagnostics.CheckServerDatabaseAsync("127.0.0.1", port, TimeSpan.FromMilliseconds(500));

        Assert.False(status.Reachable);
        Assert.Contains("nicht erreichbar", status.Message);
    }

    [Fact]
    public void Server_einstellungen_haben_standardwerte()
    {
        var settings = ServerSettings.FromEnvironment();
        Assert.False(string.IsNullOrWhiteSpace(settings.Host));
        Assert.InRange(settings.Port, 1, 65535);
    }
}
