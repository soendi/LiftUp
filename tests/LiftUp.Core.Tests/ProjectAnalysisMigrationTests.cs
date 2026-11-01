using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Bestehende Analysepunkte übernehmen Untertitel und Reihenfolge aus der Vorlage.</summary>
public sealed class ProjectAnalysisMigrationTests
{
    [Fact]
    public void Bisherige_analysepunkte_bekommen_untertitel_und_reihenfolge()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var analysis = new ProjectAnalysisService(fixture.Database, session);

        var project = projects.Create(new ProjectInput
        {
            Name = "Altbestand",
            FolderPath = Path.Combine(fixture.Root, "p1"),
            Status = ProjectStatus.Active,
            CreateFolderIfMissing = true,
        });

        analysis.Save(project.Id,
        [
            new ProjectAnalysisTextInput("Aggregat", "Aggregat", "<p>Aggregat.</p>"),
            new ProjectAnalysisTextInput("Tragmittel", "Hubzylinder / Tragmittel", "<p>Seile.</p>"),
        ]);

        // Zustand vor Version 25: keine Spalten für Untertitel und Reihenfolge.
        using (var connection = fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE ProjectAnalysisTexts DROP COLUMN Title;
                ALTER TABLE ProjectAnalysisTexts DROP COLUMN SortOrder;
                UPDATE SchemaInfo SET Version = 24;
                """;
            command.ExecuteNonQuery();
        }

        fixture.Database.EnsureCreated();

        Assert.Equal(Database.CurrentSchemaVersion, fixture.Database.GetSchemaVersion());

        var texts = analysis.Get(project.Id);
        Assert.Equal(["Aggregat", "Hubzylinder / Tragmittel"], texts.Select(text => text.Title));
        Assert.Equal(["<p>Aggregat.</p>", "<p>Seile.</p>"], texts.Select(text => text.Html));
        Assert.Equal([0, 1], texts.Select(text => text.SortOrder));
    }
}
