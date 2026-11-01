using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Anschrift der eigenen Firma am Benutzer (Deckblatt-Absender).</summary>
public sealed class UserCompanyAddressTests
{
    [Fact]
    public void Firmenanschrift_wird_gespeichert_und_gelesen()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var role = new RoleService(fixture.Database, admin).GetAll().Single(item => item.Name == "Projektleiter");
        var users = new UserService(fixture.Database, admin);

        var created = users.Create(
            "mmustermann",
            "Max Mustermann",
            role.Id,
            "Startpasswort1",
            mustChangePassword: false,
            company: "LiftUp AG",
            companyStreet: "Bahnhofstrasse 4",
            companyZip: "8000",
            companyCity: "Zürich");

        var item = users.GetUser(created.Id);
        Assert.NotNull(item);
        Assert.Equal("LiftUp AG", item!.Company);
        Assert.Equal("Bahnhofstrasse 4", item.CompanyStreet);
        Assert.Equal("8000", item.CompanyZip);
        Assert.Equal("Zürich", item.CompanyCity);
    }

    [Fact]
    public void Firmenanschrift_laesst_sich_nachtragen()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var role = new RoleService(fixture.Database, admin).GetAll().Single(item => item.Name == "Leser");
        var users = new UserService(fixture.Database, admin);

        var created = users.Create("leser", "Leser", role.Id, "Startpasswort1", mustChangePassword: false);
        users.Update(
            created.Id,
            "Leser",
            role.Id,
            isActive: true,
            company: "LiftUp AG",
            companyStreet: "Bahnhofstrasse 4",
            companyZip: "8000",
            companyCity: "Zürich");

        var item = users.GetUser(created.Id);
        Assert.NotNull(item);
        Assert.Equal("Bahnhofstrasse 4", item!.CompanyStreet);
        Assert.Equal("8000", item.CompanyZip);
        Assert.Equal("Zürich", item.CompanyCity);
    }
}
