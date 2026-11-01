using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Ansprechpersonen getrennt nach Kunde und Auftraggeber.</summary>
public sealed class ProjectClientContactsTests
{
    private static ProjectInput Input(string folder, string clientContact) => new()
    {
        Name = "Messligfeld 1 und 3, Sennewald",
        FolderPath = folder,
        Status = ProjectStatus.Active,
        CreateFolderIfMissing = true,
        Contacts =
        [
            new ProjectContactInput { Name = "Frau Meier", Responsibility = "Bauleitung" },
        ],
        ClientContacts =
        [
            new ProjectContactInput
            {
                Kind = ProjectContactKind.Client,
                Name = clientContact,
                Email = "info@example.com",
            },
        ],
    };

    [Fact]
    public void Auftraggeber_hat_eigene_ansprechpersonen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1"), "Herr Schulz"));

        var loaded = projects.GetProject(project.Id);

        var customer = Assert.Single(loaded.Contacts);
        Assert.Equal("Frau Meier", customer.Name);
        Assert.Equal(ProjectContactKind.Customer, customer.Kind);

        var client = Assert.Single(loaded.ClientContacts);
        Assert.Equal("Herr Schulz", client.Name);
        Assert.Equal(ProjectContactKind.Client, client.Kind);
        Assert.Equal("info@example.com", client.Email);

        // Der erste Kundenkontakt bleibt für Übersicht und Suche am Projekt stehen.
        Assert.Equal("Frau Meier", loaded.Contact);
    }

    [Fact]
    public void Auftraggeber_ansprechpersonen_lassen_sich_aendern()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1"), "Herr Schulz"));

        projects.Update(project.Id, Input(Path.Combine(fixture.Root, "p1"), "Frau Weber"));

        var loaded = projects.GetProject(project.Id);
        Assert.Equal("Frau Weber", Assert.Single(loaded.ClientContacts).Name);
        // Die Kundenansprechperson bleibt unberührt.
        Assert.Equal("Frau Meier", Assert.Single(loaded.Contacts).Name);
    }

    [Fact]
    public void Ohne_auftraggeber_personen_bleibt_die_liste_leer()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);

        var project = projects.Create(new ProjectInput
        {
            Name = "Ohne Personen",
            FolderPath = Path.Combine(fixture.Root, "p2"),
            Status = ProjectStatus.Active,
            CreateFolderIfMissing = true,
        });

        Assert.Empty(projects.GetProject(project.Id).ClientContacts);
    }
}
