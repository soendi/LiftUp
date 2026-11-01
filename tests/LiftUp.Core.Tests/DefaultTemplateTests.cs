using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Die mitgelieferte Vorlage „Zustandsbericht Aufzugsanlagen“ mit ihren Tabs.</summary>
public sealed class DefaultTemplateTests
{
    [Fact]
    public void Startvorlage_ist_der_Zustandsbericht()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var templates = new TemplateService(fixture.Database, session);

        var template = templates.GetAll(includeInactive: true)
            .Single(item => item.Name == "Zustandsbericht Aufzugsanlagen");

        var sections = templates.GetSections(template.Id);
        Assert.Equal(
            ["1. Anlagedaten", "2. Analyse", "3. Fazit / Empfehlung"],
            sections.Select(section => section.Title));

        // Tab 1 bringt die Tabelle mit, die anderen beiden nur ihren Titel.
        Assert.Contains("<table", sections[0].DefaultHtml);
        Assert.Contains("Aufzugstyp:", sections[0].DefaultHtml);
        Assert.Contains("Auffahrsockel:", sections[0].DefaultHtml);
        Assert.Equal(string.Empty, sections[1].DefaultHtml);
        Assert.Equal(string.Empty, sections[2].DefaultHtml);
    }

    [Fact]
    public void Bestehende_datenbank_erhaelt_den_zustandsbericht()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();

        // Zustand einer Datenbank aus Version 15: unberührte Startvorlage unter altem Namen.
        using (var connection = fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM TemplateSections
                WHERE TemplateId IN (SELECT Id FROM Templates WHERE Name = 'Zustandsbericht Aufzugsanlagen');
                DELETE FROM TemplateVersions
                WHERE TemplateId IN (SELECT Id FROM Templates WHERE Name = 'Zustandsbericht Aufzugsanlagen');
                UPDATE Templates SET Name = 'Standard-Dokument', CurrentVersion = 1, DocumentType = 'Dokument'
                WHERE Name = 'Zustandsbericht Aufzugsanlagen';
                UPDATE SchemaInfo SET Version = 15;
                """;
            command.ExecuteNonQuery();
        }

        fixture.Database.EnsureCreated();

        var templates = new TemplateService(fixture.Database, session);
        var all = templates.GetAll(includeInactive: true);

        Assert.DoesNotContain(all, item => item.Name == "Standard-Dokument");

        var report = all.Single(item => item.Name == "Zustandsbericht Aufzugsanlagen");
        var sections = templates.GetSections(report.Id);
        Assert.Equal(3, sections.Count);
        Assert.Contains("Auffahrsockel:", sections[0].DefaultHtml);
    }

    [Fact]
    public void Deckblatt_zeigt_das_projekt_zwischen_den_beiden_adressen()
    {
        using var fixture = new TestDatabase();
        var html = CurrentTemplateHtml(fixture);

        Assert.Contains("facts facts-three", html);
        Assert.Contains("<div class=\"label\">Projekt</div>", html);
        Assert.Contains("{{project.address}}", html);

        // Drei Spalten: Auftraggeber, Projekt, Projektverfasser.
        Assert.Equal(3, html.Split("<td>", StringSplitOptions.None).Length - 1);

        // Das Projektbild bekommt Abstand zur Adresszeile.
        Assert.Contains("<div class=\"cover-image\">{{project.image}}</div>", html);
        Assert.Contains(".cover-image { margin: 0 0 8mm 0; }", html);
    }

    [Fact]
    public void Bestehendes_deckblatt_erhaelt_die_dritte_spalte()
    {
        using var fixture = new TestDatabase();

        // Bisheriges Deckblatt der Version 23: zwei Spalten, Bild ohne Abstand.
        const string previousCover = """
            <!DOCTYPE html>
            <html lang="de"><head><style>.facts td { width: 50%; }</style></head>
            <body>
              <div class="brand">{{company.logo}}</div>
              <div class="kicker">Zustandsbericht</div>
              {{project.image}}
              <table class="facts">
                <tr>
                  <td><div class="label">Auftraggeber</div><strong>{{project.client}}</strong></td>
                  <td><div class="label">Projektverfasser</div><strong>{{company.name}}</strong></td>
                </tr>
              </table>
            </body></html>
            """;

        using (var connection = fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE TemplateVersions SET Html = $html
                WHERE TemplateId IN (SELECT Id FROM Templates WHERE Name = 'Zustandsbericht Aufzugsanlagen')
                  AND Version = 1;
                UPDATE SchemaInfo SET Version = 23;
                """;
            command.Parameters.AddWithValue("$html", previousCover);
            command.ExecuteNonQuery();
        }

        fixture.Database.EnsureCreated();

        var html = CurrentTemplateHtml(fixture);
        Assert.Contains("facts facts-three", html);
        Assert.Contains("{{project.address}}", html);
    }

    [Fact]
    public void Deckblatt_zeigt_das_datum_der_begehung_in_normaler_schrift()
    {
        using var fixture = new TestDatabase();
        var html = CurrentTemplateHtml(fixture);

        Assert.Contains("Die Daten wurden am {{project.inspectiondate}} vor Ort aufgenommen.", html);
        Assert.DoesNotContain("<strong>{{project.inspectiondate}}</strong>", html);
    }

    [Fact]
    public void Bestehendes_deckblatt_verliert_den_fetten_begehungstermin()
    {
        using var fixture = new TestDatabase();

        // Bisheriges Deckblatt der Version 25: Datum der Begehung in Fettschrift.
        using (var connection = fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE TemplateVersions
                SET Html = replace(Html, '{{project.inspectiondate}}', '<strong>{{project.inspectiondate}}</strong>')
                WHERE TemplateId IN (SELECT Id FROM Templates WHERE Name = 'Zustandsbericht Aufzugsanlagen')
                  AND Version = 1;
                UPDATE SchemaInfo SET Version = 25;
                """;
            command.ExecuteNonQuery();
        }

        fixture.Database.EnsureCreated();

        var html = CurrentTemplateHtml(fixture);
        Assert.DoesNotContain("<strong>{{project.inspectiondate}}</strong>", html);
        Assert.Contains("Die Daten wurden am {{project.inspectiondate}} vor Ort aufgenommen.", html);
    }

    /// <summary>Aktueller Inhalt der Vorlage „Zustandsbericht Aufzugsanlagen“.</summary>
    private static string CurrentTemplateHtml(TestDatabase fixture)
    {
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.Html
            FROM Templates t
            JOIN TemplateVersions v ON v.TemplateId = t.Id AND v.Version = t.CurrentVersion
            WHERE t.Name = 'Zustandsbericht Aufzugsanlagen';
            """;
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public void Startinhalt_bleibt_beim_Speichern_der_Titel_erhalten()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var templates = new TemplateService(fixture.Database, session);
        var template = templates.GetAll(includeInactive: true)
            .Single(item => item.Name == "Zustandsbericht Aufzugsanlagen");

        templates.SaveSections(template.Id, ["1. Anlagedaten", "2. Analyse", "3. Fazit / Empfehlung", "4. Anhang"]);

        var sections = templates.GetSections(template.Id);
        Assert.Equal(4, sections.Count);
        Assert.Contains("Aufzugstyp:", sections[0].DefaultHtml);
        Assert.Equal(string.Empty, sections[3].DefaultHtml);
    }
}
