using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Prüft die Leseansicht des Änderungsprotokolls (Reiter „Protokoll").</summary>
public sealed class ChangeLogServiceTests
{
    [Fact]
    public void Liefert_neueste_zuerst_mit_bearbeiter_und_lesbaren_bezeichnungen()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();

        Record(fixture, "Project", admin.UserName, new { Name = "Umbau Halle", Number = "2026-0001" });
        Record(fixture, "User", admin.UserName, new { Name = "anna" });

        var entries = new ChangeLogService(fixture.Database, admin).Query(new ChangeLogFilter());

        Assert.Equal(2, entries.Count);
        // Zuletzt geschrieben steht oben.
        Assert.Equal("Benutzer", entries[0].EntityText);
        Assert.Equal("Projekt", entries[1].EntityText);
        Assert.Equal("angelegt", entries[1].OperationText);
        Assert.Equal(admin.UserName, entries[1].Actor);
        Assert.Equal("offen", entries[1].SyncText);
        Assert.Contains("Umbau Halle", entries[1].ObjectText);
    }

    [Fact]
    public void Filtert_nach_bereich_und_bearbeiter()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();

        Record(fixture, "Project", admin.UserName, new { Name = "Umbau Halle" });
        Record(fixture, "Project", "anna", new { Name = "Neubau Büro" });
        Record(fixture, "Role", "anna", new { Name = "Leser" });

        var service = new ChangeLogService(fixture.Database, admin);

        Assert.Equal(2, service.Query(new ChangeLogFilter(EntityType: "Project")).Count);
        Assert.Equal(2, service.Query(new ChangeLogFilter(Actor: "anna")).Count);
        Assert.Single(service.Query(new ChangeLogFilter(EntityType: "Project", Actor: "anna")));
    }

    [Fact]
    public void Zeitraum_grenzt_auf_den_gewaehlten_tag_ein()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        Record(fixture, "Project", admin.UserName, new { Name = "Umbau Halle" });

        var service = new ChangeLogService(fixture.Database, admin);
        var today = DateTime.Now.Date;

        Assert.Single(service.Query(new ChangeLogFilter(From: today, To: today)));
        Assert.Empty(service.Query(new ChangeLogFilter(From: today.AddDays(2))));
        Assert.Empty(service.Query(new ChangeLogFilter(To: today.AddDays(-1))));
    }

    [Fact]
    public void Suche_findet_werte_in_der_nutzlast()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        Record(fixture, "Project", admin.UserName, new { Name = "Umbau Halle" });
        Record(fixture, "Project", admin.UserName, new { Name = "Neubau Büro" });

        var service = new ChangeLogService(fixture.Database, admin);

        var treffer = service.Query(new ChangeLogFilter(Search: "Halle"));
        Assert.Single(treffer);
        Assert.Contains("Umbau Halle", treffer[0].ObjectText);
    }

    [Fact]
    public void Liefert_die_bekannten_bereiche_und_bearbeiter()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        Record(fixture, "Project", admin.UserName, new { Name = "A" });
        Record(fixture, "Template", "anna", new { Name = "B" });

        var service = new ChangeLogService(fixture.Database, admin);

        Assert.Equal(["Project", "Template"], service.GetEntityTypes());
        Assert.Equal([admin.UserName, "anna"], service.GetActors());
    }

    [Fact]
    public void Ohne_recht_wird_der_zugriff_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var leser = fixture.CreateAndLogin("leser1", "Start1234!", "Leser");
        var service = new ChangeLogService(fixture.Database, leser);

        Assert.Throws<PermissionDeniedException>(() => service.Query(new ChangeLogFilter()));
        Assert.Throws<PermissionDeniedException>(() => service.GetActors());
    }

    [Fact]
    public void Nutzlast_wird_kurz_und_eingerueckt_dargestellt()
    {
        Assert.Equal("Umbau Halle · 2026-0001", ChangeLogService.Summarize(
            """{"Name":"Umbau Halle","Number":"2026-0001"}"""));
        Assert.Equal("kein JSON", ChangeLogService.Summarize("kein JSON"));
        Assert.Contains("\n", ChangeLogService.FormatPayload("""{"Name":"A"}"""));
        Assert.Equal("kaputt", ChangeLogService.FormatPayload("kaputt"));
    }

    /// <summary>Schreibt einen Protokolleintrag, ohne einen Fachdienst zu bemühen.</summary>
    private static void Record(TestDatabase fixture, string entityType, string actor, object payload)
    {
        using var connection = fixture.Database.Open();
        using var transaction = connection.BeginTransaction();
        new ChangeLogWriter(fixture.Database, actor)
            .Record(connection, transaction, entityType, Guid.NewGuid().ToString("D"), "Create", payload, 1);
        transaction.Commit();
    }
}
