using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class UserServiceTests
{
    [Fact]
    public void Benutzer_anlegen_und_anmelden()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var role = new RoleService(fixture.Database, admin).GetAll().Single(item => item.Name == "Projektleiter");
        var users = new UserService(fixture.Database, admin);

        var created = users.Create("mmustermann", "Max Mustermann", role.Id, "Startpasswort1", mustChangePassword: true);

        Assert.Equal("mmustermann", created.UserName);
        Assert.Contains(users.GetUsers(), item => item.UserName == "mmustermann");
        Assert.Equal(2, users.GetUsers().Count);

        var login = fixture.Auth.Login("mmustermann", "Startpasswort1");
        Assert.True(login.Success);
        Assert.True(login.Session!.MustChangePassword);
        Assert.True(login.Session.Can(Permission.CreateProjects));
        Assert.False(login.Session.Can(Permission.ManageUsers));
    }

    [Fact]
    public void Doppelter_Benutzername_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var role = new RoleService(fixture.Database, admin).GetAll().Single(item => item.Name == "Leser");
        var users = new UserService(fixture.Database, admin);

        users.Create("doppelt", "Doppelt", role.Id, "Startpasswort1", mustChangePassword: false);

        var exception = Assert.Throws<ValidationException>(() =>
            users.Create("doppelt", "Nochmal", role.Id, "Startpasswort1", mustChangePassword: false));
        Assert.Contains("bereits vergeben", exception.Message);
    }

    [Fact]
    public void Eigenes_Konto_kann_nicht_geloescht_werden()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();

        Assert.Throws<ValidationException>(() => new UserService(fixture.Database, admin).Delete(admin.UserId));
    }

    [Fact]
    public void Letzter_Administrator_kann_sich_nicht_selbst_entziehen()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var roles = new RoleService(fixture.Database, admin).GetAll();
        var administrator = roles.Single(item => item.Name == "Administrator");
        var betrachter = roles.Single(item => item.Name == "Leser");

        var owner = new UserService(fixture.Database, admin);
        var second = owner.Create("chef", "Chef", administrator.Id, "Startpasswort1", mustChangePassword: false);

        var chef = fixture.Auth.Login("chef", "Startpasswort1").Session!;
        var chefUsers = new UserService(fixture.Database, chef);

        // Der zweite Administrator darf den ersten herabstufen ...
        chefUsers.Update(admin.UserId, "Administrator", betrachter.Id, isActive: true);

        // ... aber danach sich selbst nicht mehr zum einfachen Benutzer machen.
        var exception = Assert.Throws<ValidationException>(() =>
            chefUsers.Update(second.Id, "Chef", betrachter.Id, isActive: true));
        Assert.Contains("letzte aktive Administrator", exception.Message);
    }

    [Fact]
    public void Ohne_Recht_keine_Benutzerverwaltung()
    {
        using var fixture = new TestDatabase();
        var viewer = fixture.CreateAndLogin("leser", "Leser12345", "Leser");

        Assert.Throws<PermissionDeniedException>(() => new UserService(fixture.Database, viewer).GetUsers());
    }

    [Fact]
    public void Passwort_zuruecksetzen_funktioniert()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var role = new RoleService(fixture.Database, admin).GetAll().Single(item => item.Name == "Leser");
        var users = new UserService(fixture.Database, admin);

        var user = users.Create("resetme", "Reset", role.Id, "Startpasswort1", mustChangePassword: false);
        users.ResetPassword(user.Id, "NeuesPasswort9", mustChangePassword: true);

        var login = fixture.Auth.Login("resetme", "NeuesPasswort9");
        Assert.True(login.Success);
        Assert.True(login.Session!.MustChangePassword);
        Assert.False(fixture.Auth.Login("resetme", "Startpasswort1").Success);
    }
}
