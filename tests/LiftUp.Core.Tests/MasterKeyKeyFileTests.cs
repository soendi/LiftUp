using System.Security.Cryptography;
using LiftUp.Core.Security;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Der öffentliche MasterKey-Schlüssel ist fest im Programm hinterlegt. Er muss der
/// Schlüsseldatei <c>public-key.bin</c> im Repository und dem in <c>public-key.txt</c>
/// ausgewiesenen Fingerabdruck entsprechen – sonst passen die Codes aus MasterKey.exe nicht.
/// </summary>
public sealed class MasterKeyKeyFileTests
{
    /// <summary>SHA-256 über die 32 Rohbytes, wie in public-key.txt ausgewiesen.</summary>
    private const string Fingerprint = "7F6E8F07557B8E65A2CA89BC3C59EB57FFAB9C6F23F20C2DF47BBBD883EAD22F";

    [Fact]
    public void Hinterlegter_schlüssel_hat_den_dokumentierten_fingerabdruck()
    {
        Assert.True(MasterKeyKeys.TryGetPublicKey(out var key), "Kein öffentlicher Schlüssel hinterlegt.");
        Assert.Equal(32, key.Length);
        Assert.Equal(Fingerprint, Convert.ToHexString(SHA256.HashData(key)));
    }

    [Fact]
    public void Hinterlegter_schlüssel_entspricht_der_schlüsseldatei()
    {
        var path = FindRepoFile("public-key.bin");
        if (path is null)
        {
            // Ausserhalb des Repositorys (etwa bei installierten Tests) nicht prüfbar.
            return;
        }

        Assert.True(MasterKeyKeys.TryGetPublicKey(out var embedded));
        Assert.Equal(File.ReadAllBytes(path), embedded);
    }

    private static string? FindRepoFile(string name)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null)
        {
            var candidate = Path.Combine(folder.FullName, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            folder = folder.Parent;
        }

        return null;
    }
}
