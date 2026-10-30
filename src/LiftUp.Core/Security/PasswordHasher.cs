using System.Security.Cryptography;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Security;

/// <summary>
/// Passwörter werden mit PBKDF2-HMAC-SHA256 und Zufallssalz gespeichert.
/// Die Iterationszahl wird pro Benutzer mitgeschrieben, damit sie später
/// erhöht werden kann, ohne bestehende Konten unbrauchbar zu machen.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;
    private const int SaltSizeBytes = 16;
    private const int KeySizeBytes = 32;

    public sealed record HashResult(string HashBase64, string SaltBase64, int Iterations);

    public static HashResult Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = DeriveKey(password, salt, iterations, KeySizeBytes);
        return new HashResult(Convert.ToBase64String(key), Convert.ToBase64String(salt), iterations);
    }

    public static bool Verify(string password, string hashBase64, string saltBase64, int iterations)
    {
        if (string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64) || iterations < 1)
        {
            return false;
        }

        byte[] expected;
        byte[] salt;
        try
        {
            expected = Convert.FromBase64String(hashBase64);
            salt = Convert.FromBase64String(saltBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length == 0)
        {
            return false;
        }

        var actual = DeriveKey(password ?? string.Empty, salt, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);
}

public static class PasswordPolicy
{
    public const int MinimumLength = 8;

    public static void Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ValidationException("Bitte ein Passwort angeben.");
        }

        if (password.Length < MinimumLength)
        {
            throw new ValidationException($"Das Passwort muss mindestens {MinimumLength} Zeichen lang sein.");
        }

        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
        {
            throw new ValidationException("Das Passwort muss Buchstaben und mindestens eine Ziffer enthalten.");
        }
    }
}
