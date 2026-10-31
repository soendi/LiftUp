using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Npgsql;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Prüft das Einlesen der vom Server-Installer erzeugten Verbindungsdatei
/// (installer/Install-ServerDatabase.ps1 → liftup-server.json).
/// </summary>
public class ServerProfileTests
{
    private const string SampleJson = """
        {
          "format": "liftup-server",
          "formatVersion": 1,
          "generatedUtc": "2026-10-31T08:15:00.0000000Z",
          "generatedBy": "Install-ServerDatabase.ps1",
          "installerVersion": "1.0.0",
          "host": "db-server.firma.local",
          "hostName": "DB-SERVER",
          "addresses": ["10.0.0.12"],
          "port": 5432,
          "database": "liftup",
          "schema": "liftup",
          "username": "liftup",
          "password": "Start-Passwort_2026",
          "sslMode": "Prefer",
          "serverVersion": "18.6",
          "schemaVersion": 13,
          "tableCount": 2,
          "tables": ["ChangeLog", "Users"],
          "connectionString": "Host=db-server.firma.local;Port=5432;Database=liftup;Username=liftup",
          "clientCidr": ["10.0.0.0/8"],
          "notes": "Enthaelt das Passwort."
        }
        """;

    [Fact]
    public void Liest_die_vom_installer_erzeugte_datei()
    {
        var profile = ServerProfile.Parse(SampleJson);

        Assert.Equal("db-server.firma.local", profile.Host);
        Assert.Equal("DB-SERVER", profile.HostName);
        Assert.Equal(5432, profile.Port);
        Assert.Equal("liftup", profile.Database);
        Assert.Equal("liftup", profile.Schema);
        Assert.Equal("liftup", profile.UserName);
        Assert.Equal("Start-Passwort_2026", profile.Password);
        Assert.Equal("Prefer", profile.SslMode);
        Assert.Equal("18.6", profile.ServerVersion);
        Assert.Equal(13, profile.SchemaVersion);
        Assert.Equal(2, profile.Tables.Count);
        Assert.Contains("ChangeLog", profile.Tables);
        Assert.Equal("10.0.0.0/8", Assert.Single(profile.ClientCidr));
        Assert.Equal("db-server.firma.local:5432", profile.Endpoint);
        Assert.True(profile.SchemaVersionMatchesLocal);
        Assert.NotEqual(default, profile.GeneratedUtc);
    }

    [Fact]
    public void Baut_den_verbindungsstring_fuer_npgsql()
    {
        var profile = ServerProfile.Parse(SampleJson);
        var builder = new NpgsqlConnectionStringBuilder(profile.BuildConnectionString());

        Assert.Equal("db-server.firma.local", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("liftup", builder.Database);
        Assert.Equal("liftup", builder.Username);
        Assert.Equal("Start-Passwort_2026", builder.Password);
        Assert.Equal("liftup", builder.SearchPath);
        Assert.Equal("LiftUp", builder.ApplicationName);
        Assert.Equal(Npgsql.SslMode.Prefer, builder.SslMode);
    }

    [Fact]
    public void Nimmt_einzelnen_text_statt_einer_liste_an()
    {
        var json = SampleJson
            .Replace("\"clientCidr\": [\"10.0.0.0/8\"]", "\"clientCidr\": \"10.0.0.0/8\"")
            .Replace("\"tables\": [\"ChangeLog\", \"Users\"]", "\"tables\": null");

        var profile = ServerProfile.Parse(json);

        Assert.Equal("10.0.0.0/8", Assert.Single(profile.ClientCidr));
        Assert.Empty(profile.Tables);
    }

    [Fact]
    public void Lehnt_fremde_dateien_ab()
    {
        var json = SampleJson.Replace("\"liftup-server\"", "\"irgendein-anderes-format\"");

        var exception = Assert.Throws<ValidationException>(() => ServerProfile.Parse(json));
        Assert.Contains("liftup-server", exception.Message);
    }

    [Fact]
    public void Lehnt_ungueltigen_port_ab()
    {
        var json = SampleJson.Replace("\"port\": 5432", "\"port\": 0");

        Assert.Throws<ValidationException>(() => ServerProfile.Parse(json));
    }

    [Fact]
    public void Lehnt_unbekannten_ssl_modus_ab()
    {
        var json = SampleJson.Replace("\"sslMode\": \"Prefer\"", "\"sslMode\": \"Egal\"");

        var exception = Assert.Throws<ValidationException>(() => ServerProfile.Parse(json));
        Assert.Contains("SSL", exception.Message);
    }

    [Fact]
    public void Lehnt_fehlende_datenbank_ab()
    {
        var json = SampleJson.Replace("\"database\": \"liftup\",", "\"database\": \"\",");

        Assert.Throws<ValidationException>(() => ServerProfile.Parse(json));
    }

    [Fact]
    public void Speichert_und_liest_die_einstellungen()
    {
        using var test = new TestDatabase();
        var store = new ServerProfileStore(test.Database);
        var profile = ServerProfile.Parse(SampleJson);

        store.Save(profile, Path.Combine(test.Root, "liftup-server.json"));

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(profile.Host, loaded!.Host);
        Assert.Equal(profile.Password, loaded.Password);
        Assert.Equal(profile.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(profile.Tables.Count, loaded.Tables.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(test.Root, "liftup-server.json")), store.GetSourcePath());

        // Das Passwort steht nicht in den übrigen Angaben.
        Assert.DoesNotContain("Start-Passwort_2026", test.Database.GetSetting("ServerProfile") ?? string.Empty);
    }

    [Fact]
    public void Entfernen_loescht_die_einstellungen()
    {
        using var test = new TestDatabase();
        var store = new ServerProfileStore(test.Database);
        store.Save(ServerProfile.Parse(SampleJson), Path.Combine(test.Root, "liftup-server.json"));

        store.Clear();

        Assert.Null(store.Load());
        Assert.Null(store.GetSourcePath());
    }
}
