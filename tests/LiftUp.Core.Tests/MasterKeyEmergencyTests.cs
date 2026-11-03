using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Security;
using LiftUp.Core.Services;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Erzeugt Testcodes genau wie MasterKey.exe: Nutzdaten, Kontext, Ed25519-Signatur und
/// Base64 URL-safe. Der private Schlüssel entsteht je Test neu und bleibt im Speicher.
/// </summary>
internal sealed class MasterKeyTestKey
{
    public MasterKeyTestKey()
    {
        Private = new Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
        PublicKey = Private.GeneratePublicKey().GetEncoded();
    }

    public Ed25519PrivateKeyParameters Private { get; }

    public byte[] PublicKey { get; }

    public string Create(
        DateTime issuedUtc,
        DateTime expiresUtc,
        byte version = MasterKeyFormat.Version1,
        int flipByteAt = -1)
    {
        var payload = new byte[MasterKeyFormat.PayloadLength];
        payload[0] = version;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), Seconds(issuedUtc));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(9, 8), Seconds(expiresUtc));

        var context = Encoding.ASCII.GetBytes(MasterKeyFormat.SignatureContext);
        var message = new byte[context.Length + payload.Length];
        context.CopyTo(message, 0);
        payload.CopyTo(message.AsSpan(context.Length));

        var signature = new byte[MasterKeyFormat.SignatureLength];
        Private.Sign(Ed25519.Algorithm.Ed25519, null, message, 0, message.Length, signature, 0);

        var code = new byte[MasterKeyFormat.CodeLength];
        payload.CopyTo(code, 0);
        signature.CopyTo(code.AsSpan(MasterKeyFormat.PayloadLength));

        if (flipByteAt >= 0)
        {
            code[flipByteAt] ^= 0x01;
        }

        return Convert.ToBase64String(code).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Code, der zum angegebenen Zeitpunkt gültig ist.</summary>
    public string Valid(DateTime nowUtc) => Create(nowUtc, nowUtc.AddHours(2));

    private static long Seconds(DateTime moment) =>
        new DateTimeOffset(DateTime.SpecifyKind(moment, DateTimeKind.Utc)).ToUnixTimeSeconds();
}

/// <summary>
/// Notfallzugang über einen signierten MasterKey-Lizenzcode: Prüfung, Rechte,
/// Passwort-Wiederherstellung, Protokollierung und Sitzungsablauf.
/// </summary>
public sealed class MasterKeyEmergencyTests
{
    private static MasterKeyTestKey NewKey() => new();

    private static EmergencyLoginResult Login(TestDatabase fixture, MasterKeyTestKey key, DateTime nowUtc) =>
        new EmergencyAuthenticationService(fixture.Database)
            .Login(key.Create(nowUtc, nowUtc.AddHours(2)), nowUtc, key.PublicKey);

    private static Session EmergencySession(TestDatabase fixture, MasterKeyTestKey key, DateTime nowUtc)
    {
        var result = Login(fixture, key, nowUtc);
        Assert.True(result.Success, result.Error);
        return result.Session!;
    }

    [Fact]
    public void Gültiger_code_erzeugt_notfall_sitzung()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;

        var result = Login(fixture, key, now);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Error);
        Assert.NotNull(result.Session);
        Assert.True(result.Session!.IsEmergency);
        Assert.Equal("admin", result.Session.UserName);
        Assert.Contains("Notfall", result.Session.RoleName);
        Assert.Equal(Permission.All, result.Session.Permissions);
    }

    [Fact]
    public void Abgelaufener_code_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;

        var result = new EmergencyAuthenticationService(fixture.Database)
            .Login(key.Create(now.AddDays(-3), now.AddDays(-2)), now, key.PublicKey);

        Assert.False(result.Success);
        Assert.Null(result.Session);
        Assert.Equal("MasterKey-Code ist abgelaufen.", result.Error);
    }

    [Fact]
    public void Noch_nicht_gültiger_code_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;

        var result = new EmergencyAuthenticationService(fixture.Database)
            .Login(key.Create(now.AddDays(2), now.AddDays(3)), now, key.PublicKey);

        Assert.False(result.Success);
        Assert.Null(result.Session);
        Assert.Equal("MasterKey-Code ist noch nicht gültig.", result.Error);
    }

    [Fact]
    public void Code_mit_uhrenversatz_von_wenigen_minuten_gilt_bereits()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;

        // Erzeugungszeitpunkt liegt eine Minute in der Zukunft – innerhalb der Toleranz.
        var result = new EmergencyAuthenticationService(fixture.Database)
            .Login(key.Create(now.AddMinutes(1), now.AddHours(1)), now, key.PublicKey);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void Manipulierter_code_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;
        var service = new EmergencyAuthenticationService(fixture.Database);

        // Nutzdatenbyte und Signaturbyte verändern – beides muss auffallen.
        var alteredPayload = service.Login(key.Create(now, now.AddHours(1), flipByteAt: 10), now, key.PublicKey);
        var alteredSignature = service.Login(key.Create(now, now.AddHours(1), flipByteAt: 30), now, key.PublicKey);

        Assert.False(alteredPayload.Success);
        Assert.Equal("MasterKey-Code wurde manipuliert oder ist beschädigt.", alteredPayload.Error);
        Assert.False(alteredSignature.Success);
        Assert.Equal("MasterKey-Code wurde manipuliert oder ist beschädigt.", alteredSignature.Error);
    }

    [Fact]
    public void Falscher_öffentlicher_schlüssel_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var issuer = NewKey();
        var foreign = NewKey();
        var now = DateTime.UtcNow;

        var result = new EmergencyAuthenticationService(fixture.Database)
            .Login(issuer.Create(now, now.AddHours(1)), now, foreign.PublicKey);

        Assert.False(result.Success);
        Assert.Null(result.Session);
        Assert.Equal("MasterKey-Code wurde manipuliert oder ist beschädigt.", result.Error);
    }

    [Fact]
    public void Beschädigter_code_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;
        var service = new EmergencyAuthenticationService(fixture.Database);
        var code = key.Valid(now);

        var shortened = service.Login(code[..100], now, key.PublicKey);
        var invalidCharacter = service.Login("!" + code[1..], now, key.PublicKey);

        Assert.False(shortened.Success);
        Assert.Equal("MasterKey-Code ist ungültig.", shortened.Error);
        Assert.False(invalidCharacter.Success);
        Assert.Equal("MasterKey-Code ist ungültig.", invalidCharacter.Error);
    }

    [Fact]
    public void Unbekanntes_format_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;

        var result = new EmergencyAuthenticationService(fixture.Database)
            .Login(key.Create(now, now.AddHours(1), version: 2), now, key.PublicKey);

        Assert.False(result.Success);
        Assert.Equal("Dieses Lizenzcode-Format wird nicht unterstützt.", result.Error);
    }

    [Fact]
    public void Unplausible_zeitstempel_werden_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;

        // Ablauf vor Erzeugung – korrekt signiert, aber inhaltlich unsinnig.
        var result = new EmergencyAuthenticationService(fixture.Database)
            .Login(key.Create(now.AddDays(1), now), now, key.PublicKey);

        Assert.False(result.Success);
        Assert.Equal("MasterKey-Code ist ungültig.", result.Error);
    }

    [Fact]
    public void Ohne_hinterlegten_schlüssel_wird_jeder_code_abgelehnt()
    {
        var key = NewKey();
        var now = DateTime.UtcNow;

        var validation = MasterKeyCodeValidator.Validate(key.Valid(now), now, []);

        Assert.False(validation.IsValid);
        Assert.Equal(MasterKeyError.NotConfigured, validation.Error);
        Assert.Contains("nicht eingerichtet", validation.Message);
    }

    [Fact]
    public void Reguläre_anmeldung_bleibt_erhalten()
    {
        using var fixture = new TestDatabase();

        var result = fixture.Auth.Login("admin", "admin");

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Session);
        Assert.False(result.Session!.IsEmergency);
        Assert.True(result.Session.MustChangePassword);
    }

    [Fact]
    public void Notfallzugang_meldet_ohne_benutzername_und_passwort_an()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;

        // Der Aufruf kennt weder Benutzername noch Passwort.
        var result = Login(fixture, key, now);

        Assert.True(result.Success, result.Error);
        Assert.Equal("admin", result.Session!.UserName);
        Assert.NotEqual("admin", result.Session.ActorName);
        Assert.Contains("Notfallzugang", result.Session.ActorName);
    }

    [Fact]
    public void Notfallzugang_hat_die_vorgesehenen_rechte()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var session = EmergencySession(fixture, key, DateTime.UtcNow);

        Assert.True(session.Can(Permission.ManageUsers));
        Assert.True(session.Can(Permission.ManageRoles));
        Assert.True(session.Can(Permission.EditProjects));

        // Die Dienste prüfen die Rechte zentral – die Ansicht entscheidet nicht selbst.
        var users = new UserService(fixture.Database, session);
        var roles = new RoleService(fixture.Database, session);
        Assert.NotEmpty(users.GetUsers());
        Assert.NotEmpty(roles.GetAll());
    }

    [Fact]
    public void Passwort_zurücksetzen_ermöglicht_wieder_anmeldung()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var session = EmergencySession(fixture, key, DateTime.UtcNow);
        var users = new UserService(fixture.Database, session);
        var admin = users.GetUsers().Single(item => item.UserName == "admin");

        users.ResetPassword(admin.Id, "WiederDa1x", mustChangePassword: false);

        var regular = fixture.Auth.Login("admin", "WiederDa1x");
        Assert.True(regular.Success, regular.Error);
        Assert.False(regular.Session!.IsEmergency);
        Assert.False(regular.Session.MustChangePassword);
    }

    [Fact]
    public void Administratorpasswort_wird_wiederhergestellt_und_sicher_gespeichert()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var session = EmergencySession(fixture, key, DateTime.UtcNow);
        var users = new UserService(fixture.Database, session);
        var admin = users.GetUsers().Single(item => item.UserName == "admin");

        users.ResetPassword(admin.Id, "ChefPasswort9", mustChangePassword: false);

        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PasswordHash, PasswordSalt, PasswordIterations FROM Users WHERE UserName = 'admin';";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.NotEqual("ChefPasswort9", reader.GetString(0));
        Assert.NotEqual(string.Empty, reader.GetString(1));
        Assert.True(reader.GetInt32(2) >= 100_000);

        Assert.True(fixture.Auth.Login("admin", "ChefPasswort9").Success);
    }

    [Fact]
    public void Neuer_administrator_kann_angelegt_werden()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var session = EmergencySession(fixture, key, DateTime.UtcNow);
        var users = new UserService(fixture.Database, session);
        var adminRole = new RoleService(fixture.Database, session).GetAll()
            .Single(role => role.Has(Permission.ManageUsers));

        var created = users.Create("helfende", "Helfende Person", adminRole.Id, "NeuStart1", mustChangePassword: true);

        Assert.Equal("helfende", created.UserName);
        var regular = fixture.Auth.Login("helfende", "NeuStart1");
        Assert.True(regular.Success, regular.Error);
        Assert.True(regular.Session!.MustChangePassword);
    }

    [Fact]
    public void Deaktiviertes_konto_kann_entsperrt_werden()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var session = EmergencySession(fixture, key, DateTime.UtcNow);
        var users = new UserService(fixture.Database, session);
        var adminRole = new RoleService(fixture.Database, session).GetAll()
            .Single(role => role.Has(Permission.ManageUsers));
        var created = users.Create("gesperrt", "Gesperrte Person", adminRole.Id, "StartPass1", mustChangePassword: false);

        using (var connection = fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Users SET IsActive = 0 WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", created.Id.ToString("D"));
            command.ExecuteNonQuery();
        }

        Assert.False(fixture.Auth.Login("gesperrt", "StartPass1").Success);

        users.Unlock(created.Id);

        Assert.True(fixture.Auth.Login("gesperrt", "StartPass1").Success);
    }

    [Fact]
    public void Ungültiger_code_gewährt_keine_rechte()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;

        var result = new EmergencyAuthenticationService(fixture.Database)
            .Login("NichtEinMasterKeyCode", now, key.PublicKey);

        Assert.False(result.Success);
        Assert.Null(result.Session);

        // Auch nichts, was später als Sitzung weiterverwendet werden könnte.
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM ChangeLog WHERE EntityType = 'EmergencyAccess';";
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void Notfall_anmeldung_wird_ohne_code_protokolliert()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;
        var code = key.Create(now, now.AddHours(2));

        var result = new EmergencyAuthenticationService(fixture.Database).Login(code, now, key.PublicKey);
        Assert.True(result.Success, result.Error);

        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Operation, Payload, OriginUser FROM ChangeLog WHERE EntityType = 'EmergencyAccess';";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("Login", reader.GetString(0));
        Assert.Contains("MasterKey", reader.GetString(1));
        Assert.DoesNotContain(code, reader.GetString(1));
        Assert.Contains("Notfallzugang", reader.GetString(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Notfall_sitzung_endet_nach_inaktivität()
    {
        using var fixture = new TestDatabase();
        var key = NewKey();
        var now = DateTime.UtcNow;
        var session = EmergencySession(fixture, key, now);

        var clock = now;
        var manager = new EmergencySessionManager(session, TimeSpan.FromMinutes(10), () => clock);

        Assert.False(manager.IsExpired);

        clock = now.AddMinutes(9);
        manager.Touch();

        clock = now.AddMinutes(17);
        Assert.False(manager.IsExpired);

        clock = now.AddMinutes(28);
        Assert.True(manager.IsExpired);

        // Eine abgelaufene Sitzung lebt nicht wieder auf.
        manager.Touch();
        Assert.True(manager.IsExpired);
        Assert.Equal(TimeSpan.Zero, manager.Remaining);
    }

    [Fact]
    public void Nur_notfall_sitzungen_werden_überwacht()
    {
        using var fixture = new TestDatabase();
        var regular = fixture.LoginAsAdmin();

        Assert.Throws<ArgumentException>(() =>
            new EmergencySessionManager(regular, TimeSpan.FromMinutes(10)));
    }
}
