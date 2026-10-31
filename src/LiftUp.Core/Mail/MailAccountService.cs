using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Mail;

/// <summary>
/// Verbindet ein Postfach per OAuth 2.0 (PKCE) und versendet Nachrichten aus
/// diesem Postfach: bei Google über die Gmail-API, bei Microsoft 365 über Graph.
/// Tokens liegen DPAPI-geschützt neben der Datenbank und gelten nur für den
/// angemeldeten Windows-Benutzer.
/// </summary>
public sealed class MailAccountService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly Database _database;
    private readonly Session _actor;
    private readonly string _tokenFolder;

    public MailAccountService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;

        var baseDirectory = Path.GetDirectoryName(database.FilePath) ?? Path.GetTempPath();
        _tokenFolder = Path.Combine(baseDirectory, "mail");
    }

    public bool IsConnected => LoadTokens()?.RefreshToken is { Length: > 0 };

    public MailAccount GetAccount()
    {
        var providerValue = _database.GetSetting(Key("Provider"));
        var provider = Enum.TryParse<MailProvider>(providerValue, out var parsed) ? parsed : MailProvider.Google;

        return new MailAccount
        {
            Provider = provider,
            ClientId = _database.GetSetting(Key("ClientId")) ?? string.Empty,
            EmailAddress = _database.GetSetting(Key("Email")) ?? string.Empty,
        };
    }

    public void SaveAccount(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        RequireClientId(account.ClientId);

        _database.SetSetting(Key("Provider"), account.Provider.ToString());
        _database.SetSetting(Key("ClientId"), account.ClientId.Trim());
    }

    public void Disconnect()
    {
        var path = TokenPath;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Baut Anmeldeadresse, PKCE-Verifier und Zustandswert für den Browser.</summary>
    public static (string Url, string Verifier, string State) BuildAuthorizationRequest(MailAccount account, string redirectUri)
    {
        ArgumentNullException.ThrowIfNull(account);
        var info = MailProviders.Get(account.Provider);
        var clientId = RequireClientId(account.ClientId);

        var verifier = CreateVerifier();
        var challenge = CreateChallenge(verifier);
        var state = Guid.NewGuid().ToString("N");

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("response_type", "code"),
            new("redirect_uri", redirectUri),
            new("scope", info.Scope),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
            new("state", state),
        };

        if (account.Provider == MailProvider.Google)
        {
            parameters.Add(new KeyValuePair<string, string>("access_type", "offline"));
            parameters.Add(new KeyValuePair<string, string>("prompt", "consent"));
        }
        else
        {
            parameters.Add(new KeyValuePair<string, string>("prompt", "consent"));
        }

        var query = string.Join("&", parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));

        return (info.AuthorizationEndpoint + "?" + query, verifier, state);
    }

    /// <summary>Tauscht den Anmeldecode gegen Tokens und merkt sich das Postfach.</summary>
    public async Task<MailAccount> CompleteLoginAsync(
        MailAccount account,
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ValidationException("Die Anmeldung lieferte keinen Code.");
        }

        var info = MailProviders.Get(account.Provider);
        var clientId = RequireClientId(account.ClientId);

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
        };

        if (info.SendScopeOnToken)
        {
            form["scope"] = info.Scope;
        }

        var tokens = await PostFormAsync(info.TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            throw new ValidationException(
                "Der Anbieter hat keine dauerhafte Freigabe geliefert. Bitte die Anmeldung erneut starten und den Zugriff bestätigen.");
        }

        var email = await FetchEmailAsync(info, tokens.AccessToken, cancellationToken).ConfigureAwait(false);
        SaveTokens(new StoredTokens
        {
            RefreshToken = tokens.RefreshToken,
            AccessToken = tokens.AccessToken,
            ExpiresUtc = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn),
        });

        SaveAccount(account);
        _database.SetSetting(Key("Email"), email);

        account.EmailAddress = email;
        return account;
    }

    /// <summary>Gültiges Zugangstoken; erneuert es bei Bedarf über das Refresh-Token.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var tokens = LoadTokens()
                     ?? throw new ValidationException("Bitte zuerst ein E-Mail-Konto verbinden.");

        if (!string.IsNullOrWhiteSpace(tokens.AccessToken) && tokens.ExpiresUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return tokens.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            throw new ValidationException("Der Zugriff ist abgelaufen. Bitte das E-Mail-Konto erneut verbinden.");
        }

        var account = GetAccount();
        var info = MailProviders.Get(account.Provider);
        var clientId = RequireClientId(account.ClientId);

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
        };

        if (info.SendScopeOnToken)
        {
            form["scope"] = info.Scope;
        }

        var response = await PostFormAsync(info.TokenEndpoint, form, cancellationToken).ConfigureAwait(false);

        tokens.AccessToken = response.AccessToken;
        tokens.ExpiresUtc = DateTime.UtcNow.AddSeconds(response.ExpiresIn);
        if (!string.IsNullOrWhiteSpace(response.RefreshToken))
        {
            tokens.RefreshToken = response.RefreshToken;
        }

        SaveTokens(tokens);
        return tokens.AccessToken;
    }

    /// <summary>Versendet die Nachricht aus dem verbundenen Postfach.</summary>
    public async Task SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        _actor.Require(Permission.CreateDocuments);
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.To))
        {
            throw new ValidationException("Bitte eine Empfängeradresse angeben.");
        }

        var account = GetAccount();
        if (!IsConnected)
        {
            throw new ValidationException("Bitte zuerst ein E-Mail-Konto verbinden.");
        }

        var accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (account.Provider == MailProvider.Google)
        {
            await SendViaGmailAsync(account, accessToken, message, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SendViaGraphAsync(accessToken, message, cancellationToken).ConfigureAwait(false);
        }
    }

    public static string CreateVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return MailMimeBuilder.ToBase64Url(bytes);
    }

    public static string CreateChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return MailMimeBuilder.ToBase64Url(hash);
    }

    private async Task SendViaGmailAsync(
        MailAccount account,
        string accessToken,
        MailMessage message,
        CancellationToken cancellationToken)
    {
        var raw = MailMimeBuilder.BuildRaw(message, account.EmailAddress);
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["raw"] = MailMimeBuilder.ToBase64Url(Encoding.UTF8.GetBytes(raw)),
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://gmail.googleapis.com/gmail/v1/users/me/messages/send")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ValidationException("Gmail hat den Versand abgelehnt: " + DescribeError(text));
        }
    }

    private async Task SendViaGraphAsync(
        string accessToken,
        MailMessage message,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["subject"] = message.Subject ?? string.Empty,
            ["body"] = new Dictionary<string, object?>
            {
                ["contentType"] = "HTML",
                ["content"] = message.BodyHtml ?? string.Empty,
            },
            ["toRecipients"] = BuildRecipients(message.To),
        };

        if (!string.IsNullOrWhiteSpace(message.Cc))
        {
            body["ccRecipients"] = BuildRecipients(message.Cc);
        }

        var attachment = ReadAttachment(message.AttachmentPath);
        if (attachment is not null)
        {
            body["attachments"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"] = attachment.Value.FileName,
                    ["contentType"] = "application/pdf",
                    ["contentBytes"] = Convert.ToBase64String(attachment.Value.Bytes),
                },
            };
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["message"] = body,
            ["saveToSentItems"] = true,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/sendMail")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ValidationException("Microsoft 365 hat den Versand abgelehnt: " + DescribeError(text));
        }
    }

    private static object[] BuildRecipients(string? addresses) =>
        (addresses ?? string.Empty)
        .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(address => address.Length > 0)
        .Select(address => new Dictionary<string, object?>
        {
            ["emailAddress"] = new Dictionary<string, string> { ["address"] = address },
        })
        .ToArray();

    private static (string FileName, byte[] Bytes)? ReadAttachment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return (Path.GetFileName(path), File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ValidationException("Der Anhang konnte nicht gelesen werden: " + exception.Message);
        }
    }

    private static async Task<string> FetchEmailAsync(
        MailProviderInfo info,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, info.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.TryGetProperty("email", out var email) && email.GetString() is { Length: > 0 } address)
            {
                return address;
            }

            if (root.TryGetProperty("mail", out var mail) && mail.ValueKind == JsonValueKind.String &&
                mail.GetString() is { Length: > 0 } mailAddress)
            {
                return mailAddress;
            }

            if (root.TryGetProperty("userPrincipalName", out var upn) && upn.GetString() is { Length: > 0 } principal)
            {
                return principal;
            }
        }
        catch (JsonException)
        {
            // Ohne Adresse bleibt das Feld leer; der Versand funktioniert trotzdem.
        }

        return string.Empty;
    }

    private static async Task<TokenResponse> PostFormAsync(
        string endpoint,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        if (!root.TryGetProperty("access_token", out var access) ||
            string.IsNullOrWhiteSpace(access.GetString()))
        {
            var description = root.TryGetProperty("error_description", out var detail)
                ? detail.GetString()
                : root.TryGetProperty("error", out var error) ? error.GetString() : text;
            throw new ValidationException("Anmeldung fehlgeschlagen: " + Truncate(description));
        }

        var refresh = root.TryGetProperty("refresh_token", out var refreshElement) &&
                      refreshElement.ValueKind == JsonValueKind.String
            ? refreshElement.GetString()
            : null;

        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) &&
                        expiresElement.TryGetInt32(out var seconds)
            ? seconds
            : 3600;

        return new TokenResponse(access.GetString()!, refresh, expiresIn);
    }

    private static string DescribeError(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message))
                {
                    return Truncate(message.GetString());
                }

                return Truncate(error.ValueKind == JsonValueKind.String ? error.GetString() : error.ToString());
            }
        }
        catch (JsonException)
        {
            // Kein JSON: Rohtext anzeigen.
        }

        return Truncate(text);
    }

    private static string Truncate(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Unbekannter Fehler."
            : value.Length <= 300 ? value : value[..300] + " …";

    private static string RequireClientId(string? clientId)
    {
        var trimmed = (clientId ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ValidationException(
                "Bitte die Client-ID der eigenen App-Registrierung eintragen (Google Cloud bzw. Azure).");
        }

        return trimmed;
    }

    private string Key(string suffix) => $"Mail.{_actor.UserId:N}.{suffix}";

    private string TokenPath => Path.Combine(_tokenFolder, $"{_actor.UserId:N}.token");

    private StoredTokens? LoadTokens()
    {
        var path = TokenPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var plain = Unprotect(encrypted);
            return JsonSerializer.Deserialize<StoredTokens>(plain);
        }
        catch (Exception exception) when (
            exception is IOException or CryptographicException or JsonException or ValidationException)
        {
            return null;
        }
    }

    private void SaveTokens(StoredTokens tokens)
    {
        Directory.CreateDirectory(_tokenFolder);
        var json = JsonSerializer.SerializeToUtf8Bytes(tokens);
        File.WriteAllBytes(TokenPath, Protect(json));
    }

    private static byte[] Protect(byte[] data)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ValidationException("Der E-Mail-Versand ist nur unter Windows verfügbar.");
        }

        return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    }

    private static string Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ValidationException("Der E-Mail-Versand ist nur unter Windows verfügbar.");
        }

        return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
    }

    private sealed class StoredTokens
    {
        public string? RefreshToken { get; set; }
        public string? AccessToken { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    private sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn);
}
