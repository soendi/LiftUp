using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Backup.Cloud;

/// <summary>Anzeige für die Gerätecode-Anmeldung.</summary>
public sealed record DeviceCodePrompt(string UserCode, string VerificationUrl, string Message, DateTime ExpiresUtc);

public sealed class CloudBackupService
{
    private const string OneDriveClientIdKey = "Backup.OneDrive.ClientId";
    private const string GoogleClientIdKey = "Backup.Google.ClientId";
    private const string GoogleClientSecretKey = "Backup.Google.ClientSecret";

    // Microsoft Graph: Schreibzugriff nur im eigenen App-Ordner, kein Zugriff auf andere Dateien.
    private const string OneDriveScope = "offline_access Files.ReadWrite.AppFolder";
    private const string OneDriveDeviceEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/devicecode";
    private const string OneDriveTokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string OneDriveUploadRoot = "https://graph.microsoft.com/v1.0/me/drive/special/approot:/LiftUp/";

    // Google Drive: appdata-Ordner, für den Benutzer unsichtbar.
    private const string GoogleScope = "https://www.googleapis.com/auth/drive.appdata";
    private const string GoogleDeviceEndpoint = "https://oauth2.googleapis.com/device/code";
    private const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string GoogleUploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id,name";

    private readonly Database _database;
    private readonly HttpClient _http;
    private readonly CloudTokenStore _tokens;

    public CloudBackupService(Database database, HttpClient? http = null, string? tokenFolder = null)
    {
        _database = database;
        _http = http ?? new HttpClient();
        var baseDirectory = Path.GetDirectoryName(database.FilePath) ?? Path.GetTempPath();
        _tokens = new CloudTokenStore(tokenFolder ?? Path.Combine(baseDirectory, "cloud"));
    }

    public bool IsConfigured(BackupTargetKind kind) => kind switch
    {
        BackupTargetKind.OneDrive => !string.IsNullOrWhiteSpace(GetClientId(kind)),
        BackupTargetKind.GoogleDrive => !string.IsNullOrWhiteSpace(GetClientId(kind)) &&
                                        !string.IsNullOrWhiteSpace(GetClientSecret(kind)),
        _ => false,
    };

    public string GetClientId(BackupTargetKind kind) =>
        _database.GetSetting(kind == BackupTargetKind.GoogleDrive ? GoogleClientIdKey : OneDriveClientIdKey) ?? string.Empty;

    public string GetClientSecret(BackupTargetKind kind) =>
        _database.GetSetting(GoogleClientSecretKey) ?? string.Empty;

    public void SaveConfiguration(BackupTargetKind kind, string clientId, string? clientSecret = null)
    {
        _database.SetSetting(
            kind == BackupTargetKind.GoogleDrive ? GoogleClientIdKey : OneDriveClientIdKey,
            (clientId ?? string.Empty).Trim());

        if (kind == BackupTargetKind.GoogleDrive)
        {
            _database.SetSetting(GoogleClientSecretKey, (clientSecret ?? string.Empty).Trim());
        }
    }

    public CloudTokenSet? GetAccount(BackupTargetKind kind) => _tokens.Load(kind);

    public void SignOut(BackupTargetKind kind) => _tokens.Delete(kind);

    /// <summary>
    /// Startet die Anmeldung: liefert einen Code, den der Benutzer auf der
    /// Anbieterseite eingibt, und wartet dann auf die Freigabe.
    /// </summary>
    public async Task<CloudTokenSet> SignInAsync(
        BackupTargetKind kind,
        IProgress<DeviceCodePrompt>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ValidationException("Die Cloud-Anmeldung ist nur unter Windows verfügbar.");
        }

        if (!IsConfigured(kind))
        {
            throw new ValidationException(kind == BackupTargetKind.GoogleDrive
                ? "Bitte zuerst Client-ID und Client-Schlüssel der Google-Anwendung hinterlegen."
                : "Bitte zuerst die Client-ID der Microsoft-Anwendung hinterlegen.");
        }

        var clientId = GetClientId(kind);
        var isGoogle = kind == BackupTargetKind.GoogleDrive;

        var deviceForm = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = isGoogle ? GoogleScope : OneDriveScope,
        };

        if (isGoogle)
        {
            deviceForm["client_secret"] = GetClientSecret(kind);
        }

        using var device = await PostFormAsync(
            isGoogle ? GoogleDeviceEndpoint : OneDriveDeviceEndpoint,
            deviceForm,
            cancellationToken).ConfigureAwait(false);

        var deviceCode = device.RootElement.GetProperty("device_code").GetString() ?? string.Empty;
        var userCode = device.RootElement.GetProperty("user_code").GetString() ?? string.Empty;
        var verification = device.RootElement.TryGetProperty("verification_uri", out var uriElement)
            ? uriElement.GetString()
            : device.RootElement.GetProperty("verification_url").GetString();
        var expiresIn = device.RootElement.TryGetProperty("expires_in", out var expiresElement)
            ? expiresElement.GetInt32()
            : 900;
        var interval = device.RootElement.TryGetProperty("interval", out var intervalElement)
            ? intervalElement.GetInt32()
            : 5;

        var expiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);
        var message = device.RootElement.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString() ?? string.Empty
            : $"Code {userCode} auf {verification} eingeben.";

        progress?.Report(new DeviceCodePrompt(userCode, verification ?? string.Empty, message, expiresUtc));

        var tokenForm = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
        };

        if (isGoogle)
        {
            tokenForm["client_secret"] = GetClientSecret(kind);
        }

        var wait = Math.Max(interval, 2);

        while (DateTime.UtcNow < expiresUtc)
        {
            await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);

            using var token = await TryPostFormAsync(
                isGoogle ? GoogleTokenEndpoint : OneDriveTokenEndpoint,
                tokenForm,
                cancellationToken).ConfigureAwait(false);

            if (token is null)
            {
                continue;
            }

            if (token.RootElement.TryGetProperty("access_token", out var accessElement))
            {
                var tokens = new CloudTokenSet
                {
                    AccessToken = accessElement.GetString() ?? string.Empty,
                    RefreshToken = token.RootElement.TryGetProperty("refresh_token", out var refreshElement)
                        ? refreshElement.GetString()
                        : null,
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(
                        token.RootElement.TryGetProperty("expires_in", out var secondsElement)
                            ? secondsElement.GetInt32()
                            : 3600),
                    AccountName = await ReadAccountNameAsync(kind, tokens: null, accessToken: accessElement.GetString(), cancellationToken)
                        .ConfigureAwait(false) ?? string.Empty,
                };

                _tokens.Save(kind, tokens);
                return tokens;
            }

            var error = token.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;

            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    wait += 5;
                    continue;
                case "authorization_declined":
                case "access_denied":
                    throw new ValidationException("Die Anmeldung wurde abgelehnt.");
                case "expired_token":
                    throw new ValidationException("Der Anmeldecode ist abgelaufen. Bitte erneut starten.");
                default:
                    throw new ValidationException($"Die Anmeldung ist fehlgeschlagen: {error ?? "unbekannter Fehler"}.");
            }
        }

        throw new ValidationException("Die Anmeldung wurde nicht rechtzeitig bestätigt.");
    }

    /// <summary>Lädt eine Sicherung in den App-Ordner des angemeldeten Kontos.</summary>
    public async Task UploadAsync(BackupTargetKind kind, string filePath, CancellationToken cancellationToken = default)
    {
        if (kind == BackupTargetKind.LocalFolder)
        {
            return;
        }

        var token = await GetValidTokenAsync(kind, cancellationToken).ConfigureAwait(false);
        var fileName = Path.GetFileName(filePath);
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);

        using var request = kind == BackupTargetKind.GoogleDrive
            ? BuildGoogleUpload(fileName, bytes, token)
            : BuildOneDriveUpload(fileName, bytes, token);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ValidationException(
                $"Der Upload ist fehlgeschlagen ({response.StatusCode}): {Shorten(body)}");
        }
    }

    private async Task<string?> ReadAccountNameAsync(
        BackupTargetKind kind,
        CloudTokenSet? tokens,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        var token = accessToken ?? tokens?.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var url = kind == BackupTargetKind.GoogleDrive
            ? "https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)"
            : "https://graph.microsoft.com/v1.0/me?$select=displayName,userPrincipalName";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            if (kind == BackupTargetKind.GoogleDrive)
            {
                return document.RootElement.TryGetProperty("user", out var user)
                       && user.TryGetProperty("emailAddress", out var email)
                    ? email.GetString()
                    : null;
            }

            return document.RootElement.TryGetProperty("userPrincipalName", out var principal)
                ? principal.GetString()
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<string> GetValidTokenAsync(BackupTargetKind kind, CancellationToken cancellationToken)
    {
        var tokens = _tokens.Load(kind)
                     ?? throw new ValidationException("Für diese Cloud ist keine Anmeldung hinterlegt.");

        if (!tokens.IsExpired)
        {
            return tokens.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            throw new ValidationException("Die Anmeldung ist abgelaufen. Bitte erneut anmelden.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = GetClientId(kind),
            ["refresh_token"] = tokens.RefreshToken,
        };

        if (kind == BackupTargetKind.GoogleDrive)
        {
            form["client_secret"] = GetClientSecret(kind);
        }

        using var response = await PostFormAsync(
            kind == BackupTargetKind.GoogleDrive ? GoogleTokenEndpoint : OneDriveTokenEndpoint,
            form,
            cancellationToken).ConfigureAwait(false);

        var accessToken = response.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
        tokens.AccessToken = accessToken;
        tokens.ExpiresUtc = DateTime.UtcNow.AddSeconds(
            response.RootElement.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3600);

        if (response.RootElement.TryGetProperty("refresh_token", out var refresh))
        {
            tokens.RefreshToken = refresh.GetString();
        }

        _tokens.Save(kind, tokens);
        return accessToken;
    }

    private static HttpRequestMessage BuildOneDriveUpload(string fileName, byte[] bytes, string accessToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{OneDriveUploadRoot}{Uri.EscapeDataString(fileName)}:/content")
        {
            Content = new ByteArrayContent(bytes),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage BuildGoogleUpload(string fileName, byte[] bytes, string accessToken)
    {
        const string boundary = "liftup-backup-boundary";
        var metadata = JsonSerializer.Serialize(new
        {
            name = fileName,
            parents = new[] { "appDataFolder" },
        });

        var prefix = $"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{metadata}\r\n" +
                     $"--{boundary}\r\nContent-Type: application/octet-stream\r\n\r\n";
        var suffix = $"\r\n--{boundary}--\r\n";

        var payload = new List<byte>(bytes.Length + prefix.Length + suffix.Length);
        payload.AddRange(Encoding.UTF8.GetBytes(prefix));
        payload.AddRange(bytes);
        payload.AddRange(Encoding.UTF8.GetBytes(suffix));

        var content = new ByteArrayContent(payload.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/related; boundary={boundary}");

        var request = new HttpRequestMessage(HttpMethod.Post, GoogleUploadEndpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<JsonDocument> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ValidationException($"Der Anbieter hat abgelehnt ({response.StatusCode}): {Shorten(body)}");
        }

        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument?> TryPostFormAsync(string url, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && !body.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
        {
            return JsonDocument.Parse(body);
        }

        return JsonDocument.Parse(body);
    }

    private static string Shorten(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";

    public static string ProviderName(BackupTargetKind kind) => kind switch
    {
        BackupTargetKind.GoogleDrive => "Google Drive",
        BackupTargetKind.OneDrive => "OneDrive",
        _ => "Lokaler Ordner",
    };

    public static string FormatExpiry(CloudTokenSet tokens) =>
        tokens.ExpiresUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
}
