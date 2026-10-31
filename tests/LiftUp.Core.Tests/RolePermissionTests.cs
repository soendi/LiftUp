using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class RolePermissionTests
{
    [Fact]
    public void Administrator_erhaelt_nachtraeglich_hinzugekommene_rechte()
    {
        using var fixture = new TestDatabase();

        // Datenbank aus Version 7 nachstellen: dem Administrator fehlt das Recht auf Textbausteine.
        using (var connection = fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE Roles SET Permissions = Permissions & ~$manageSnippets
                WHERE Name = 'Administrator' AND IsSystem = 1;
                UPDATE SchemaInfo SET Version = 7;
                """;
            command.Parameters.AddWithValue("$manageSnippets", (int)Permission.ManageSnippets);
            command.ExecuteNonQuery();
        }

        fixture.Database.EnsureCreated();

        var admin = fixture.LoginAsAdmin();
        Assert.True(admin.Can(Permission.ManageSnippets));

        var snippet = new SnippetService(fixture.Database, admin).Create(new SnippetInput
        {
            Name = "Anschreiben",
            Html = "<p>Guten Tag</p>",
        });

        Assert.Equal("Anschreiben", snippet.Name);
    }

    [Fact]
    public void Fehlendes_recht_nennt_die_rolle_beim_namen()
    {
        using var fixture = new TestDatabase();
        var leser = fixture.CreateAndLogin("leser", "Leser12345", "Leser");

        var exception = Assert.Throws<PermissionDeniedException>(() =>
            new SnippetService(fixture.Database, leser).Create(new SnippetInput
            {
                Name = "Anschreiben",
                Html = "<p>Guten Tag</p>",
            }));

        Assert.Contains("(Leser)", exception.Message);
        Assert.Contains("Textbausteine verwalten", exception.Message);
    }
}
