using LiftUp.Core.Data;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Services;

/// <summary>
/// Eigene Absenderdaten für Titelseiten. Sie gelten für alle Vorlagen und
/// werden in der Tabelle <c>Settings</c> gespeichert.
/// </summary>
public sealed class CompanyProfileService
{
    private const string NameKey = "Company.Name";
    private const string ContactKey = "Company.Contact";
    private const string EmailKey = "Company.Email";
    private const string PhoneKey = "Company.Phone";

    private readonly Database _database;
    private readonly Session? _actor;

    public CompanyProfileService(Database database, Session? actor = null)
    {
        _database = database;
        _actor = actor;
    }

    /// <summary>Liest die Absenderdaten; ohne gepflegte Angaben bleiben die Felder leer.</summary>
    public CompanyProfile Get()
    {
        using var connection = _database.Open();
        using var command = Db.Command(connection, """
            SELECT Key, Value FROM Settings
            WHERE Key IN ('Company.Name', 'Company.Contact', 'Company.Email', 'Company.Phone');
            """);

        var profile = new CompanyProfile();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.Text(1);
            switch (reader.Text(0))
            {
                case NameKey:
                    profile.Name = value;
                    break;
                case ContactKey:
                    profile.Contact = value;
                    break;
                case EmailKey:
                    profile.Email = value;
                    break;
                case PhoneKey:
                    profile.Phone = value;
                    break;
            }
        }

        return profile;
    }

    public void Save(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _actor?.Require(Permission.ChangeSettings);

        _database.SetSetting(NameKey, (profile.Name ?? string.Empty).Trim());
        _database.SetSetting(ContactKey, (profile.Contact ?? string.Empty).Trim());
        _database.SetSetting(EmailKey, (profile.Email ?? string.Empty).Trim());
        _database.SetSetting(PhoneKey, (profile.Phone ?? string.Empty).Trim());
    }
}
