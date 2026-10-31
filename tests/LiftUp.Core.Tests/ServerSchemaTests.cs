using System.Text.RegularExpressions;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Hält das lokale Schema und das Server-Schema zusammen: kommt in der Anwendung
/// eine Tabelle oder Spalte dazu, muss sie auch in installer/sql/liftup-schema.sql
/// stehen. Sonst fehlt sie auf dem PostgreSQL-Server, sobald eine Installation
/// aktualisiert wird.
/// </summary>
public sealed class ServerSchemaTests
{
    [Fact]
    public void Server_schema_kennt_alle_lokalen_tabellen_und_spalten()
    {
        using var fixture = new TestDatabase();
        var local = fixture.Database.GetSchemaColumns();

        Assert.NotEmpty(local);

        var server = ParseServerSchema(File.ReadAllText(FindSchemaFile()));

        foreach (var (table, columns) in local)
        {
            Assert.True(
                server.ContainsKey(table),
                $"Tabelle \"{table}\" fehlt in installer/sql/liftup-schema.sql. " +
                "Neue Tabellen gehören im selben Schritt auch auf den Server.");

            foreach (var column in columns)
            {
                Assert.True(
                    server[table].Contains(column),
                    $"Spalte \"{column}\" der Tabelle \"{table}\" fehlt in installer/sql/liftup-schema.sql. " +
                    "Neue Spalten gehören im selben Schritt auch auf den Server.");
            }
        }
    }

    [Fact]
    public void Schemastand_im_server_schema_passt_zur_anwendung()
    {
        var sql = File.ReadAllText(FindSchemaFile());

        // Der Installer liest die Version anschließend aus der Tabelle; sie muss
        // deshalb beim Einspielen auf Database.CurrentSchemaVersion stehen.
        Assert.Contains($"SELECT {LiftUp.Core.Data.Database.CurrentSchemaVersion}", sql, StringComparison.Ordinal);
    }

    /// <summary>Sucht die Schemadatei vom Testausgabeverzeichnis aufwärts.</summary>
    private static string FindSchemaFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; level < 10 && directory is not null; level++)
        {
            var candidate = Path.Combine(directory.FullName, "installer", "sql", "liftup-schema.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "installer/sql/liftup-schema.sql wurde nicht gefunden; der Test muss im Projektordner laufen.");
    }

    /// <summary>
    /// Liest Tabellen und Spalten aus den CREATE-TABLE-Blöcken. Zusätzlich werden
    /// per ALTER TABLE nachgezogene Spalten berücksichtigt.
    /// </summary>
    private static Dictionary<string, HashSet<string>> ParseServerSchema(string sql)
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        var create = new Regex(
            "CREATE TABLE IF NOT EXISTS \"(?<table>[^\"]+)\" \\((?<body>.*?)\\n\\);",
            RegexOptions.Singleline);
        foreach (Match match in create.Matches(sql))
        {
            var columns = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match column in Regex.Matches(
                         match.Groups["body"].Value, "^\\s*\"(?<name>[^\"]+)\"\\s", RegexOptions.Multiline))
            {
                columns.Add(column.Groups["name"].Value);
            }

            tables[match.Groups["table"].Value] = columns;
        }

        var alter = new Regex(
            "ALTER TABLE \"(?<table>[^\"]+)\" ADD COLUMN IF NOT EXISTS \"(?<column>[^\"]+)\"");
        foreach (Match match in alter.Matches(sql))
        {
            if (tables.TryGetValue(match.Groups["table"].Value, out var columns))
            {
                columns.Add(match.Groups["column"].Value);
            }
        }

        return tables;
    }
}
