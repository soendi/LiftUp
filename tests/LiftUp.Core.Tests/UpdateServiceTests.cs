using System.Net;
using System.Security.Cryptography;
using System.Text;
using LiftUp.Core.Updates;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class UpdateServiceTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v1.4.2",
          "body": "Fehlerbehebungen",
          "assets": [
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.test/SHA256SUMS.txt", "size": 80 },
            { "name": "LiftUp-Setup.msi", "browser_download_url": "https://example.test/LiftUp-Setup.msi", "size": 4096 }
          ]
        }
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private static UpdateService CreateService(Version version, Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new("owner", "repo", version, new HttpClient(new StubHandler(responder)));

    [Fact]
    public async Task Neuere_Version_wird_erkannt()
    {
        var service = CreateService(new Version(1, 0, 0), _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson, Encoding.UTF8, "application/json"),
        });

        var result = await service.CheckAsync();

        Assert.True(result.UpdateAvailable);
        Assert.NotNull(result.Package);
        Assert.Equal(new Version(1, 4, 2), result.Package!.Version);
        Assert.Equal("LiftUp-Setup.msi", result.Package.Installer.Name);
        Assert.NotNull(result.Package.Checksums);
        Assert.Contains("1.4.2", result.Message);
    }

    [Fact]
    public async Task Gleiche_oder_aeltere_Version_ist_aktuell()
    {
        var service = CreateService(new Version(1, 4, 2), _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson, Encoding.UTF8, "application/json"),
        });

        var result = await service.CheckAsync();

        Assert.False(result.UpdateAvailable);
        Assert.Contains("aktuell", result.Message);
    }

    [Fact]
    public async Task Ohne_Release_kein_Update()
    {
        var service = CreateService(new Version(1, 0, 0), _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await service.CheckAsync();

        Assert.False(result.UpdateAvailable);
        Assert.Contains("noch kein Release", result.Message);
    }

    [Fact]
    public async Task Serverfehler_wird_als_Hinweis_gemeldet()
    {
        var service = CreateService(new Version(1, 0, 0), _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await service.CheckAsync();

        Assert.False(result.UpdateAvailable);
        Assert.Contains("502", result.Message);
    }

    [Fact]
    public void Pruefsumme_wird_aus_der_Datei_gelesen()
    {
        const string content = """
            aaaa1111  LiftUp-1.4.2.msi
            BBBB2222 *LiftUp-Setup.msi
            """;

        Assert.Equal("bbbb2222", UpdateService.ReadExpectedChecksum(content, "LiftUp-Setup.msi"));
        Assert.Null(UpdateService.ReadExpectedChecksum(content, "Unbekannt.msi"));
    }

    [Fact]
    public void Pruefsumme_erkennt_manipulierte_Datei()
    {
        var path = Path.Combine(Path.GetTempPath(), $"liftup-check-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "inhalt");

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            Assert.True(UpdateService.VerifyChecksum(path, hash));
            Assert.False(UpdateService.VerifyChecksum(path, new string('0', 64)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Update_Versuch_wird_gebremst()
    {
        var path = Path.Combine(Path.GetTempPath(), $"liftup-attempt-{Guid.NewGuid():N}.json");
        var store = new UpdateAttemptStore(path);
        var version = new Version(1, 5, 0);

        Assert.True(store.ShouldAttempt(version));
        store.Record(version);
        Assert.False(store.ShouldAttempt(version));
        Assert.True(store.ShouldAttempt(new Version(1, 6, 0)));

        File.Delete(path);
    }
}
