using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiftUp.Core.Updates;

/// <summary>Fest hinterlegtes Veröffentlichungsziel für die Silent-Updates.</summary>
public static class GitHubReleaseSource
{
    public const string Owner = "soendi";
    public const string Repository = "LiftUp";
}

public sealed record ReleaseAsset(string Name, string DownloadUrl, long SizeBytes);

public sealed record UpdatePackage(
    Version Version,
    string TagName,
    string Notes,
    ReleaseAsset Installer,
    ReleaseAsset? Checksums);

public sealed record UpdateCheckResult(bool UpdateAvailable, UpdatePackage? Package, string Message)
{
    public static UpdateCheckResult UpToDate(string message) => new(false, null, message);
    public static UpdateCheckResult Available(UpdatePackage package) => new(true, package, $"Version {package.Version} ist verfügbar.");
}

/// <summary>
/// Prüft GitHub-Releases, lädt das MSI herunter, verifiziert die Prüfsumme und
/// startet die stille Installation. Bewusst ohne zusätzliche Abhängigkeiten.
/// </summary>
public sealed class UpdateService
{
    public const string InstallerAssetName = "LiftUp-Setup.msi";
    public const string ChecksumsAssetName = "SHA256SUMS.txt";
    public const string DisableAutoUpdateVariable = "LIFTUP_DISABLE_AUTOUPDATE";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repository;

    public UpdateService(
        string owner,
        string repository,
        Version currentVersion,
        HttpClient? httpClient = null)
    {
        _owner = owner;
        _repository = repository;
        CurrentVersion = currentVersion;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LiftUp/{currentVersion}");
        }
    }

    public Version CurrentVersion { get; }

    /// <summary>Stille Installation und Neustart funktionieren nur unter Windows.</summary>
    public static bool SilentInstallSupported => OperatingSystem.IsWindows();

    /// <summary>Abschaltbar über LIFTUP_DISABLE_AUTOUPDATE=1, z. B. in verwalteten Netzen.</summary>
    public static bool AutoUpdateAllowed =>
        SilentInstallSupported && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DisableAutoUpdateVariable));

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"https://api.github.com/repos/{_owner}/{_repository}/releases/latest", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.UpToDate("Es ist noch kein Release veröffentlicht.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.UpToDate($"GitHub antwortete mit {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, SerializerOptions);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return UpdateCheckResult.UpToDate("Die Release-Angaben konnten nicht gelesen werden.");
            }

            if (!TryParseVersion(release.TagName, out var version))
            {
                return UpdateCheckResult.UpToDate($"Die Versionsangabe \"{release.TagName}\" ist unbrauchbar.");
            }

            if (Normalize(version).CompareTo(Normalize(CurrentVersion)) <= 0)
            {
                return UpdateCheckResult.UpToDate($"Version {CurrentVersion} ist aktuell.");
            }

            var assets = release.Assets ?? [];
            var installer = assets.FirstOrDefault(asset =>
                                string.Equals(asset.Name, InstallerAssetName, StringComparison.OrdinalIgnoreCase))
                            ?? assets.FirstOrDefault(asset =>
                                asset.Name?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true);

            if (installer?.BrowserDownloadUrl is null)
            {
                return UpdateCheckResult.UpToDate($"Version {version} enthält kein Installationspaket.");
            }

            var checksums = assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase));

            var package = new UpdatePackage(
                version,
                release.TagName,
                release.Body ?? string.Empty,
                new ReleaseAsset(installer.Name ?? InstallerAssetName, installer.BrowserDownloadUrl, installer.Size),
                checksums?.BrowserDownloadUrl is null
                    ? null
                    : new ReleaseAsset(checksums.Name ?? ChecksumsAssetName, checksums.BrowserDownloadUrl, checksums.Size));

            return UpdateCheckResult.Available(package);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return UpdateCheckResult.UpToDate($"Update-Prüfung fehlgeschlagen: {exception.Message}");
        }
    }

    public async Task<string> DownloadInstallerAsync(
        UpdatePackage package,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var directory = Path.Combine(Path.GetTempPath(), "LiftUp");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"LiftUp-Setup-{package.Version}.msi");

        using (var response = await _http.GetAsync(
                   package.Installer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? package.Installer.SizeBytes;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(target);

            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                if (total > 0)
                {
                    progress?.Report(Math.Min(1d, (double)written / total));
                }
            }
        }

        if (package.Checksums is not null)
        {
            var expected = await ReadChecksumAsync(package.Checksums.DownloadUrl, package.Installer.Name, cancellationToken);
            if (expected is null)
            {
                File.Delete(target);
                throw new InvalidOperationException("Im Release fehlt die Prüfsumme für das Installationspaket.");
            }

            if (!VerifyChecksum(target, expected))
            {
                File.Delete(target);
                throw new InvalidOperationException("Die Prüfsumme des Installationspakets stimmt nicht. Update abgebrochen.");
            }
        }

        return target;
    }

    /// <summary>Startet das MSI still und öffnet LiftUp anschließend wieder.</summary>
    public static bool LaunchSilentInstall(string installerPath)
    {
        if (!SilentInstallSupported || !File.Exists(installerPath))
        {
            return false;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        // Batchdatei statt verschachtelter Anführungszeichen: zuverlässiger in cmd.exe.
        var script = Path.Combine(Path.GetTempPath(), $"liftup-update-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, $"""
            @echo off
            timeout /t 2 /nobreak >nul
            msiexec /i "{installerPath}" /qn /norestart
            start "" "{executable}"
            del "%~f0"
            """);

        var startInfo = new ProcessStartInfo(script)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        return Process.Start(startInfo) is not null;
    }

    public static string? ReadExpectedChecksum(string checksumFileContent, string assetName)
    {
        foreach (var line in checksumFileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[^1].TrimStart('*');
            if (name.EndsWith(assetName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    public static bool VerifyChecksum(string filePath, string expectedSha256)
    {
        if (!File.Exists(filePath) || string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return string.Equals(hash, expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    private async Task<string?> ReadChecksumAsync(string url, string assetName, CancellationToken cancellationToken)
    {
        var content = await _http.GetStringAsync(url, cancellationToken);
        return ReadExpectedChecksum(content, assetName);
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var cleaned = tag.Trim().TrimStart('v', 'V');
        if (Version.TryParse(cleaned, out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

/// <summary>
/// Merkt sich, für welche Version bereits ein Update versucht wurde, damit ein
/// fehlschlagender Installer nicht bei jedem Start erneut einen Neustart auslöst.
/// </summary>
public sealed class UpdateAttemptStore(string? filePath = null)
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);

    private readonly string _path = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiftUp", "update-attempt.json");

    public bool ShouldAttempt(Version version)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return true;
            }

            var record = JsonSerializer.Deserialize<Attempt>(File.ReadAllText(_path));
            if (record is null || string.IsNullOrWhiteSpace(record.Version))
            {
                return true;
            }

            return !(string.Equals(record.Version, version.ToString(), StringComparison.Ordinal) &&
                     DateTime.UtcNow - record.AttemptedUtc < Cooldown);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void Record(Version version)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(new Attempt
            {
                Version = version.ToString(),
                AttemptedUtc = DateTime.UtcNow,
            }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ein nicht schreibbarer Merker darf das Update nicht verhindern.
        }
    }

    private sealed class Attempt
    {
        public string Version { get; set; } = string.Empty;
        public DateTime AttemptedUtc { get; set; }
    }
}
