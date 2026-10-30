using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class FolderServiceTests
{
    [Fact]
    public void Leerer_pfad_ist_ungueltig()
    {
        var normalized = FolderService.Normalize("   ", out var error);
        Assert.Equal(string.Empty, normalized);
        Assert.NotNull(error);
    }

    [Fact]
    public void Relativer_pfad_wird_absolut()
    {
        var normalized = FolderService.Normalize("unterordner/test", out var error);
        Assert.Null(error);
        Assert.True(Path.IsPathRooted(normalized));
    }

    [Fact]
    public void Dateipfad_ist_kein_ordner()
    {
        var file = Path.Combine(Path.GetTempPath(), $"liftup-datei-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            FolderService.Normalize(file, out var error);
            Assert.NotNull(error);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Ordner_wird_angelegt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "liftup-tests", Guid.NewGuid().ToString("N"), "tief", "tiefer");
        try
        {
            FolderService.EnsureCreated(folder);
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(folder)!))
            {
                Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(folder)!)!, recursive: true);
            }
        }
    }
}
