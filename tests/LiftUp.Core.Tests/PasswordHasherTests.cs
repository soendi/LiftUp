using LiftUp.Core.Security;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Richtiges_Passwort_wird_bestaetigt()
    {
        var hash = PasswordHasher.Hash("Geheim123");
        Assert.True(PasswordHasher.Verify("Geheim123", hash.HashBase64, hash.SaltBase64, hash.Iterations));
    }

    [Fact]
    public void Falsches_Passwort_wird_abgelehnt()
    {
        var hash = PasswordHasher.Hash("Geheim123");
        Assert.False(PasswordHasher.Verify("geheim123", hash.HashBase64, hash.SaltBase64, hash.Iterations));
    }

    [Fact]
    public void Gleiches_Passwort_ergibt_unterschiedliche_Hashes()
    {
        var first = PasswordHasher.Hash("Geheim123");
        var second = PasswordHasher.Hash("Geheim123");
        Assert.NotEqual(first.SaltBase64, second.SaltBase64);
        Assert.NotEqual(first.HashBase64, second.HashBase64);
    }

    [Fact]
    public void Kaputter_Hash_wird_abgelehnt_statt_zu_werfen()
    {
        Assert.False(PasswordHasher.Verify("Geheim123", "kein-base64!!", "auch-nicht", 1000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("kurz1")]
    [InlineData("ohnedigit")]
    public void Policy_lehnt_schwache_Passwoerter_ab(string password)
    {
        Assert.Throws<Core.Domain.ValidationException>(() => PasswordPolicy.Validate(password));
    }

    [Fact]
    public void Policy_akzeptiert_starkes_Passwort()
    {
        PasswordPolicy.Validate("Projekt2026");
    }
}
