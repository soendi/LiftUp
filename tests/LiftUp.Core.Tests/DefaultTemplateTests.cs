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
