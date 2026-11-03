using System.Buffers.Binary;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace LiftUp.Core.Security;

/// <summary>
/// Aufbau des MasterKey-Lizenzcodes, wie ihn MasterKey.exe erzeugt: 1 Byte Formatversion,
/// 8 Bytes Erzeugungs- und 8 Bytes Ablaufzeitpunkt (Unix-Sekunden in UTC, Big-Endian),
/// dahinter 64 Bytes Ed25519-Signatur. Die Signatur wird über den Kontext
/// <see cref="SignatureContext"/> und die 17 Nutzdatenbytes gebildet. Das Ganze wird als
/// Base64 URL-safe ohne Padding übertragen – das ergibt 108 Zeichen.
/// </summary>
public static class MasterKeyFormat
{
    /// <summary>Einzige unterstützte Formatversion.</summary>
    public const byte Version1 = 1;

    /// <summary>Formatversion, Erzeugungs- und Ablaufzeitpunkt zusammen.</summary>
    public const int PayloadLength = 17;

    public const int SignatureLength = 64;

    /// <summary>Nutzlast und Signatur zusammen (81 Bytes).</summary>
    public const int CodeLength = PayloadLength + SignatureLength;

    /// <summary>Länge des kodierten Codes in Zeichen.</summary>
    public const int EncodedLength = 108;

    /// <summary>Kontext, der vor die Nutzdaten gesetzt und mit signiert wird.</summary>
    public const string SignatureContext = "MASTERKEY-LICENSE-V1";

    /// <summary>Der Code gilt fünf Minuten vor dem Erzeugungszeitpunkt bereits als gültig (Uhrenversatz).</summary>
    public const int NotBeforeToleranceMinutes = 5;
}

/// <summary>Ergebnis der kryptografischen Prüfung eines MasterKey-Codes.</summary>
public enum MasterKeyError
{
    None,
    InvalidCode,
    UnsupportedVersion,
    Tampered,
    Expired,
    NotYetValid,
    NotConfigured,
}

/// <summary>
/// Ergebnis einer Prüfung inklusive der geprüften Zeitstempel. Die Meldung ist bewusst
/// verständlich und enthält keine Schlüsselwerte oder kryptografischen Details.
/// </summary>
public sealed record MasterKeyValidation(
    MasterKeyError Error,
    DateTime? IssuedUtc = null,
    DateTime? ExpiresUtc = null)
{
    public bool IsValid => Error == MasterKeyError.None;

    public string Message => Error switch
    {
        MasterKeyError.None => string.Empty,
        MasterKeyError.UnsupportedVersion => "Dieses Lizenzcode-Format wird nicht unterstützt.",
        MasterKeyError.Tampered => "MasterKey-Code wurde manipuliert oder ist beschädigt.",
        MasterKeyError.Expired => "MasterKey-Code ist abgelaufen.",
        MasterKeyError.NotYetValid => "MasterKey-Code ist noch nicht gültig.",
        MasterKeyError.NotConfigured =>
            "Der MasterKey-Notfallzugang ist in dieser Installation nicht eingerichtet. "
            + "Bitte den öffentlichen Schlüssel eintragen.",
        _ => "MasterKey-Code ist ungültig.",
    };
}

/// <summary>
/// Der öffentliche Ed25519-Schlüssel der Schlüsselstelle. Der private Schlüssel liegt
/// ausschliesslich bei MasterKey.exe und ist in LiftUp nicht enthalten. Solange hier kein
/// Schlüssel steht, lehnt LiftUp jeden Code ab (fail-closed).
/// </summary>
public static class MasterKeyKeys
{
    /// <summary>
    /// Öffentlicher Ed25519-Schlüssel als Base64 (32 Bytes) aus MasterKey.exe.
    /// Fingerabdruck (SHA-256): 7F6E8F07557B8E65A2CA89BC3C59EB57FFAB9C6F23F20C2DF47BBBD883EAD22F.
    /// Siehe docs/MasterKey-Notfallzugang.md.
    /// </summary>
    public const string EmergencyPublicKeyBase64 = "uLqDxYoMmqVkXkxzCAvI2txYVfy7xPkV42wJnyT4jtg=";

    public static bool IsConfigured => TryGetPublicKey(out _);

    public static bool TryGetPublicKey(out byte[] publicKey)
    {
        publicKey = [];
        if (string.IsNullOrWhiteSpace(EmergencyPublicKeyBase64))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(EmergencyPublicKeyBase64.Trim());
            if (bytes.Length != 32)
            {
                return false;
            }

            publicKey = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Prüft einen MasterKey-Lizenzcode vollständig offline. Die Reihenfolge entspricht der
/// Vorgabe: Format, Dekodierung, Formatversion, Nutzdaten, Signatur, Plausibilität der
/// Zeitstempel und zuletzt der Gültigkeitszeitraum. Erst wenn alles stimmt, gilt der Code.
/// </summary>
public static class MasterKeyCodeValidator
{
    public static MasterKeyValidation Validate(string? code, DateTime? nowUtc = null, byte[]? publicKey = null)
    {
        // 1) Eingabeformat prüfen: 108 Zeichen Base64 URL-safe.
        var text = (code ?? string.Empty).Trim();
        if (text.Length != MasterKeyFormat.EncodedLength)
        {
            return new MasterKeyValidation(MasterKeyError.InvalidCode);
        }

        foreach (var character in text)
        {
            if (!IsBase64UrlCharacter(character))
            {
                return new MasterKeyValidation(MasterKeyError.InvalidCode);
            }
        }

        // 2) Code dekodieren.
        var bytes = DecodeBase64Url(text);
        if (bytes is null || bytes.Length != MasterKeyFormat.CodeLength)
        {
            return new MasterKeyValidation(MasterKeyError.InvalidCode);
        }

        // 3) Formatversion prüfen.
        if (bytes[0] != MasterKeyFormat.Version1)
        {
            return new MasterKeyValidation(MasterKeyError.UnsupportedVersion);
        }

        // 4) Nutzdaten auslesen.
        var payload = bytes.AsSpan(0, MasterKeyFormat.PayloadLength);
        var issued = ReadTimestamp(payload[1..]);
        var expires = ReadTimestamp(payload[9..]);

        // 5) Signatur mit dem öffentlichen Schlüssel prüfen.
        if (publicKey is null)
        {
            if (!MasterKeyKeys.TryGetPublicKey(out var embedded))
            {
                return new MasterKeyValidation(MasterKeyError.NotConfigured, issued, expires);
            }

            publicKey = embedded;
        }

        if (publicKey.Length != 32)
        {
            return new MasterKeyValidation(MasterKeyError.NotConfigured, issued, expires);
        }

        var context = Encoding.ASCII.GetBytes(MasterKeyFormat.SignatureContext);
        var signed = new byte[context.Length + payload.Length];
        context.CopyTo(signed, 0);
        payload.CopyTo(signed.AsSpan(context.Length));

        var signature = bytes.AsSpan(MasterKeyFormat.PayloadLength, MasterKeyFormat.SignatureLength);
        if (!Ed25519.Verify(signature, publicKey, signed))
        {
            return new MasterKeyValidation(MasterKeyError.Tampered, issued, expires);
        }

        // 6) Zeitstempel auf Plausibilität prüfen.
        if (expires < issued || issued < new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        {
            return new MasterKeyValidation(MasterKeyError.InvalidCode, issued, expires);
        }

        // 7) Aktuelle Zeit mit dem Gültigkeitszeitraum vergleichen.
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        if (now > expires)
        {
            return new MasterKeyValidation(MasterKeyError.Expired, issued, expires);
        }

        if (now < issued.AddMinutes(-MasterKeyFormat.NotBeforeToleranceMinutes))
        {
            return new MasterKeyValidation(MasterKeyError.NotYetValid, issued, expires);
        }

        return new MasterKeyValidation(MasterKeyError.None, issued, expires);
    }

    private static bool IsBase64UrlCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';

    private static byte[]? DecodeBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static DateTime ReadTimestamp(ReadOnlySpan<byte> bytes)
    {
        var seconds = BinaryPrimitives.ReadInt64BigEndian(bytes);
        if (seconds < 0 || seconds > 253_402_300_799)
        {
            return DateTime.MinValue;
        }

        return DateTime.UnixEpoch.AddSeconds(seconds);
    }
}
