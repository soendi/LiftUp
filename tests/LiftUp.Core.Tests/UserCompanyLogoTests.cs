using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Firmenlogo im Benutzerprofil: Kopie in den Logoordner und Platzhalter im Deckblatt.</summary>
public sealed class UserCompanyLogoTests
{
    private static string WriteImage(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    [Fact]
    public void Firmenlogo_wird_in_den_logoordner_kopiert()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var role = new RoleService(fixture.Database, admin).GetAll().Single(item => item.Name == "Projektleiter");
        var users = new UserService(fixture.Database, admin);

        var source = WriteImage(Path.Combine(fixture.Root, "quelle"), "logo.png");
        var created = users.Create(
            "logo", "Logo Test", role.Id, "Startpasswort1", mustChangePassword: false,
            company: "Lifteratung UP GmbH", companyLogo: source);

        var expected = Path.Combine(
            Path.GetDirectoryName(fixture.Database.FilePath)!, "logos", created.Id.ToString("D") + ".png");
        Assert.Equal(expected, created.CompanyLogo);
        Assert.True(File.Exists(expected));
        Assert.True(File.Exists(source));

        var item = users.GetUser(created.Id);
        Assert.NotNull(item);
        Assert.Equal(expected, item!.CompanyLogo);
    }

    [Fact]
    public void Logo_ohne_bildformat_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var role = new RoleService(fixture.Database, admin).GetAll().Single(item => item.Name == "Leser");
        var users = new UserService(fixture.Database, admin);

        var text = WriteImage(Path.Combine(fixture.Root, "quelle"), "hinweis.txt");

        Assert.Throws<ValidationException>(() => users.Create(
            "ohne", "Ohne", role.Id, "Startpasswort1", mustChangePassword: false, companyLogo: text));
    }

    [Fact]
    public void Firmenlogo_steht_im_deckblatt_als_bild()
    {
        using var fixture = new TestDatabase();
        var path = WriteImage(Path.Combine(fixture.Root, "logo"), "marke.png");

        var company = new CompanyProfile
        {
            Name = "Lifteratung UP GmbH",
            LogoPath = path,
        };

        var html = PlaceholderRenderer.Render(
            "{{company.logo}}",
            new PlaceholderContext
            {
                Project = new Project { Name = "Messligfeld 1 und 3" },
                UserName = "admin",
                Company = company,
            });

        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("height:14mm", html);
    }
}
