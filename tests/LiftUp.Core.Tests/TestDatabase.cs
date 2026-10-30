using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Legt für jeden Test eine frische SQLite-Datei in einem Temp-Ordner an.</summary>
public sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        Root = Path.Combine(Path.GetTempPath(), "liftup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Database = new Database(Path.Combine(Root, "liftup.db"));
        Database.EnsureCreated();
        Auth = new AuthService(Database);
    }

    public string Root { get; }
    public Database Database { get; }
    public AuthService Auth { get; }

    public Session LoginAsAdmin()
    {
        var result = Auth.Login("admin", "admin");
        Assert.True(result.Success, result.Error);
        return result.Session!;
    }

    public Session CreateAndLogin(string userName, string password, string roleName)
    {
        var admin = LoginAsAdmin();
        var roles = new RoleService(Database, admin).GetAll();
        var role = roles.Single(item => item.Name == roleName);
        new UserService(Database, admin).Create(userName, userName, role.Id, password, mustChangePassword: false);

        var result = Auth.Login(userName, password);
        Assert.True(result.Success, result.Error);
        return result.Session!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Aufräumen ist für den Testausgang nicht entscheidend.
        }
    }
}
