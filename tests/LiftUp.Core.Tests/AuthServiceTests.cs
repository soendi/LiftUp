using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class AuthServiceTests
{
    [Fact]
    public void Startkonto_kann_sich_anmelden_und_muss_Passwort_aendern()
    {
        using var fixture = new TestDatabase();
        var result = fixture.Auth.Login("admin", "admin");

        Assert.True(result.Success);
        Assert.NotNull(result.Session);
        Assert.True(result.Session!.MustChangePassword);
        Assert.True(result.Session.Can(Permission.ManageUsers));
        Assert.True(result.Session.Can(Permission.ManageRoles));
    }

    [Fact]
    public void Falsches_Passwort_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var result = fixture.Auth.Login("admin", "falsch");

        Assert.False(result.Success);
        Assert.Null(result.Session);
        Assert.Equal("Benutzername oder Passwort ist falsch.", result.Error);
    }

    [Fact]
    public void Unbekannter_Benutzer_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        Assert.False(fixture.Auth.Login("gibtesnicht", "egal1234").Success);
    }

    [Fact]
    public void Passwortwechsel_mit_falschem_aktuellen_Passwort_scheitert()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();

        Assert.Throws<ValidationException>(() =>
            fixture.Auth.ChangePassword(session, "falsch", "NeuesPasswort1"));
    }

    [Fact]
    public void Passwortwechsel_hebt_Aenderungspflicht_auf()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();

        var updated = fixture.Auth.ChangePassword(session, "admin", "NeuesPasswort1");
        Assert.False(updated.MustChangePassword);

        Assert.True(fixture.Auth.Login("admin", "NeuesPasswort1").Success);
        Assert.False(fixture.Auth.Login("admin", "admin").Success);
    }

    [Fact]
    public void Deaktiviertes_Konto_kann_sich_nicht_anmelden()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var role = new RoleService(fixture.Database, admin).GetAll().Single(item => item.Name == "Leser");
        var users = new UserService(fixture.Database, admin);

        var user = users.Create("testuser", "Test Benutzer", role.Id, "Startpasswort1", mustChangePassword: false);
        users.Update(user.Id, "Test Benutzer", role.Id, isActive: false);

        var result = fixture.Auth.Login("testuser", "Startpasswort1");
        Assert.False(result.Success);
        Assert.Contains("deaktiviert", result.Error);
    }
}
