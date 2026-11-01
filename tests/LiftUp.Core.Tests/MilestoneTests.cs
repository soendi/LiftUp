using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Meilensteine eines Projekts und die Angaben auf dem Deckblatt.</summary>
public sealed class MilestoneTests
{
    private static ProjectInput Input(string folder, params ProjectMilestoneInput[] milestones) => new()
    {
        Name = "Messligfeld 1 und 3, Sennewald",
        FolderPath = folder,
        Status = ProjectStatus.Active,
        CreateFolderIfMissing = true,
        Milestones = milestones,
    };

    [Fact]
    public void Abgehakter_meilenstein_bekommt_das_heutige_datum()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);

        var project = projects.Create(Input(
            Path.Combine(fixture.Root, "p1"),
            new ProjectMilestoneInput { Title = "Begehung", IsDone = true }));

        var milestone = Assert.Single(project.Milestones);
        Assert.Equal("Begehung", milestone.Title);
        Assert.True(milestone.IsDone);
        Assert.Equal(DateTime.Today, milestone.DoneDate);

        var loaded = projects.GetProject(project.Id);
        Assert.Equal(DateTime.Today, Assert.Single(loaded.Milestones).DoneDate);
    }

    [Fact]
    public void Meilenstein_ohne_bezeichnung_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);

        Assert.Throws<ValidationException>(() => projects.Create(Input(
            Path.Combine(fixture.Root, "p1"),
            new ProjectMilestoneInput { IsDone = true })));

        Assert.Empty(projects.Search(new ProjectQuery()));
    }

    [Fact]
    public void Meilensteine_lassen_sich_ohne_projekt_update_pflegen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));

        projects.SaveMilestones(project.Id, [
            new ProjectMilestoneInput { Title = "Begehung", IsDone = true, DoneDate = new DateTime(2026, 7, 30) },
            new ProjectMilestoneInput { Title = "Bericht" },
        ]);

        var loaded = projects.GetProject(project.Id);
        Assert.Equal(2, loaded.Milestones.Count);
        Assert.Equal(ProjectMilestoneCatalog.Inspection, loaded.Milestones[0].Title);
        Assert.True(loaded.Milestones[0].IsDone);
        Assert.Equal("Bericht", loaded.Milestones[1].Title);

        // Ein eigener Meilenstein lässt sich löschen, die Begehung bleibt.
        projects.SaveMilestones(project.Id, [
            new ProjectMilestoneInput { Title = "Begehung", IsDone = true, DoneDate = new DateTime(2026, 7, 30) },
        ]);

        Assert.Equal(
            ProjectMilestoneCatalog.Inspection,
            Assert.Single(projects.GetProject(project.Id).Milestones).Title);
    }

    [Fact]
    public void Begehung_ist_immer_vorhanden_und_fest()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var folder = Path.Combine(fixture.Root, "p1");

        // Ohne Angaben entsteht trotzdem die Begehung.
        var project = projects.Create(Input(folder));
        var milestone = Assert.Single(project.Milestones);
        Assert.Equal(ProjectMilestoneCatalog.Inspection, milestone.Title);
        Assert.False(milestone.IsDone);

        // Ein anderer Name wird auf „Begehung“ zurückgesetzt, das Datum bleibt.
        projects.Update(project.Id, Input(folder, new ProjectMilestoneInput
        {
            Title = "Begehung vor Ort",
            IsDone = true,
            DoneDate = new DateTime(2026, 7, 30),
        }));

        var updated = Assert.Single(projects.GetProject(project.Id).Milestones);
        Assert.Equal(ProjectMilestoneCatalog.Inspection, updated.Title);
        Assert.Equal(new DateTime(2026, 7, 30), updated.DoneDate);

        // Wegnehmen lässt sie sich nicht: beim nächsten Speichern ist sie wieder da.
        projects.Update(project.Id, Input(folder));
        Assert.Equal(
            ProjectMilestoneCatalog.Inspection,
            Assert.Single(projects.GetProject(project.Id).Milestones).Title);
    }

    [Fact]
    public void Eigene_meilensteine_stehen_neben_der_begehung()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);

        var project = projects.Create(Input(
            Path.Combine(fixture.Root, "p1"),
            new ProjectMilestoneInput { Title = "Auswertung", IsDone = true, DoneDate = new DateTime(2026, 8, 1) },
            new ProjectMilestoneInput { Title = "Bericht" }));

        var loaded = projects.GetProject(project.Id);
        Assert.Equal(3, loaded.Milestones.Count);
        Assert.Equal(ProjectMilestoneCatalog.Inspection, loaded.Milestones[0].Title);
        Assert.Equal("Auswertung", loaded.Milestones[1].Title);
        Assert.Equal("Bericht", loaded.Milestones[2].Title);
    }

    [Fact]
    public void Begehungsdatum_und_adressen_stehen_auf_dem_deckblatt()
    {
        var project = new Project
        {
            Name = "Messligfeld 1 und 3, Sennewald",
            Number = "2026-0014",
            Client = "Rinmobas Anlagestiftung",
            ClientStreet = "Lagerstrasse 4",
            ClientZip = "9200",
            ClientCity = "Gossau",
        };
        project.Milestones.Add(new ProjectMilestone
        {
            Title = "Begehung",
            IsDone = true,
            DoneDate = new DateTime(2026, 7, 30),
        });

        var company = new CompanyProfile
        {
            Name = "Lifteratung UP GmbH",
            Street = "Rietstrasse 56",
            Zip = "9443",
            City = "Widnau",
            Contact = "Fabian Benz",
            Phone = "076 298 38 51",
            Email = "fabian.benz@lifteratungup.ch",
        };

        var html = PlaceholderRenderer.Render(
            "{{project.client}}|{{project.clientaddress}}|{{company.name}}|{{company.address}}|"
            + "{{company.contact}}|{{company.phone}}|{{company.email}}|{{project.inspectiondate}}|"
            + "{{company.city}}|{{project.milestones}}",
            new PlaceholderContext
            {
                Project = project,
                UserName = company.Contact,
                Company = company,
                Now = new DateTime(2026, 8, 21),
            });

        Assert.Equal(
            "Rinmobas Anlagestiftung|Lagerstrasse 4, 9200 Gossau|Lifteratung UP GmbH|"
            + "Rietstrasse 56, 9443 Widnau|Fabian Benz|076 298 38 51|"
            + "fabian.benz@lifteratungup.ch|30.07.2026|Widnau|Begehung: erledigt am 30.07.2026",
            html);
    }

    [Fact]
    public void Projektbild_wird_als_bild_eingesetzt()
    {
        using var fixture = new TestDatabase();
        var folder = Path.Combine(fixture.Root, "bilder");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "deckblatt.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);

        var project = new Project { Name = "Mit Bild", ImagePath = path };

        var html = PlaceholderRenderer.Render(
            "{{project.image}}",
            new PlaceholderContext { Project = project, UserName = "admin" });

        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("object-fit:cover", html);
    }
}
