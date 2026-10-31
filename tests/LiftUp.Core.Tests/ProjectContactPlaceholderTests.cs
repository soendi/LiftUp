using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Platzhalter rund um die Kundendaten in HTML-Vorlagen.</summary>
public sealed class ProjectContactPlaceholderTests
{
    [Fact]
    public void Anschrift_und_ansprechpersonen_werden_eingesetzt()
    {
        var project = new Project
        {
            Name = "Schule Nord",
            Customer = "Stadt Musterstadt",
            CustomerStreet = "Hauptstraße 12",
            CustomerZip = "12345",
            CustomerCity = "Musterstadt",
        };
        project.Contacts.Add(new ProjectContact
        {
            Name = "Frau Meier",
            Responsibility = "Bauleitung",
            Phone = "0151 1234",
            Email = "meier@example.com",
        });

        var html = PlaceholderRenderer.Render(
            "{{project.address}}|{{project.city}}|{{project.contacts}}|{{project.contact}}",
            new PlaceholderContext { Project = project, UserName = "admin" });

        const string person = "Frau Meier · Bauleitung · 0151 1234 · meier@example.com";
        Assert.Equal($"Hauptstraße 12, 12345 Musterstadt|Musterstadt|{person}|{person}", html);
    }

    [Fact]
    public void Ohne_ansprechpersonen_bleiben_die_platzhalter_leer()
    {
        var project = new Project { Name = "Leer" };

        var html = PlaceholderRenderer.Render(
            "{{project.contacts}}|{{project.address}}|{{project.contact}}",
            new PlaceholderContext { Project = project, UserName = "admin" });

        Assert.Equal("||", html);
    }

    [Fact]
    public void Mehrere_ansprechpersonen_stehen_untereinander()
    {
        var project = new Project { Name = "Schule Nord" };
        project.Contacts.Add(new ProjectContact { Name = "Frau Meier", Responsibility = "Bauleitung" });
        project.Contacts.Add(new ProjectContact { Name = "Herr Schulz", Email = "schulz@example.com" });

        var html = PlaceholderRenderer.Render(
            "{{project.contacts}}",
            new PlaceholderContext { Project = project, UserName = "admin" });

        Assert.Equal("Frau Meier · Bauleitung<br />Herr Schulz · schulz@example.com", html);
    }
}
