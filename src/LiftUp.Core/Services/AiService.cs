using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiftUp.Core.Ai;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Sync;

namespace LiftUp.Core.Services;

/// <summary>
/// Die verbundene KI: Anbieter, Modell, Adresse und der Schlüssel bzw. das Zugangstoken.
/// Der Schlüssel liegt geschützt in der Datenbank (unter Windows an den Benutzer gebunden)
/// und wird nur beim Aufruf entschlüsselt.
/// </summary>
public sealed record AiConnection(
    AiProvider Provider,
    string Model,
    string BaseUrl,
    AiAuthMode AuthMode,
    string Secret)
{
    /// <summary>Ob sich damit arbeiten lässt: Adresse und Modell vorhanden, Schlüssel falls nötig.</summary>
    public bool IsConfigured =>
        Model.Trim().Length > 0
        && BaseUrl.Trim().Length > 0
        && (!Provider.NeedsKey || Secret.Length > 0);
}

/// <summary>
/// Fragt eine KI nach einer Überarbeitung eines Textes und prüft die Verbindung. Die
/// Antwort wird als reiner Text zurückgegeben; Vorgaben und Absätze bringt der Aufrufer mit.
/// </summary>
public sealed class AiService
{
    private const string ProviderKey = "Ai.Provider";
    private const string ModelKey = "Ai.Model";
    private const string BaseUrlKey = "Ai.BaseUrl";
    private const string AuthModeKey = "Ai.AuthMode";
    private const string SecretKey = "Ai.Secret";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(120) };

    private readonly Database _database;

    public AiService(Database database) => _database = database;

    /// <summary>Die gespeicherte Verbindung; ohne Eintrag der erste Anbieter (Groq).</summary>
    public AiConnection Load()
    {
        var provider = AiProviders.Find(_database.GetSetting(ProviderKey));
        var model = (_database.GetSetting(ModelKey) ?? string.Empty).Trim();
        var baseUrl = (_database.GetSetting(BaseUrlKey) ?? string.Empty).Trim();
        var mode = string.Equals(_database.GetSetting(AuthModeKey), nameof(AiAuthMode.Token),
            StringComparison.OrdinalIgnoreCase)
            ? AiAuthMode.Token
            : AiAuthMode.ApiKey;

        return new AiConnection(
            provider,
            model.Length > 0 ? model : provider.DefaultModel,
            baseUrl.Length > 0 ? baseUrl : provider.BaseUrl,
            mode,
            LocalSecretProtector.Unprotect(_database.GetSetting(SecretKey)));
    }

    /// <summary>Merkt sich die Verbindung; ein leerer Schlüssel entfernt den bisherigen.</summary>
    public void Save(AiConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var model = (connection.Model ?? string.Empty).Trim();
        var baseUrl = (connection.BaseUrl ?? string.Empty).Trim();

        _database.SetSetting(ProviderKey, connection.Provider.Key);
        _database.SetSetting(ModelKey, model.Length > 0 ? model : connection.Provider.DefaultModel);
        _database.SetSetting(BaseUrlKey, baseUrl.Length > 0 ? baseUrl : connection.Provider.BaseUrl);
        _database.SetSetting(AuthModeKey, connection.AuthMode.ToString());
        _database.SetSetting(SecretKey, LocalSecretProtector.Protect(connection.Secret));
    }

    /// <summary>Ob im Reiter „KI“ eine Verbindung hinterlegt ist.</summary>
    public bool IsConfigured => Load().IsConfigured;

    /// <summary>
    /// Schickt den Text mit der Anweisung der Aktion an die KI und liefert die Antwort als
    /// reinen Text. Fehler des Anbieters kommen als <see cref="LiftUpException"/> mit dessen
    /// Meldung an, damit der Benutzer weiss, was zu tun ist (Schlüssel, Modell, Guthaben).
    /// </summary>
    public async Task<string> AskAsync(AiTextAction action, string text, CancellationToken cancellationToken)
    {
        var connection = Load();
        if (!connection.IsConfigured)
        {
            throw new ValidationException(
                "Es ist keine KI verbunden. Im Reiter „KI“ einen Anbieter wählen und den Schlüssel hinterlegen.");
        }

        // Bei Stichworten nennt die Anfrage den Inhalt beim Namen; sonst ist es ein Text.
        var heading = action == AiTextAction.FromKeywords ? "Stichworte:" : "Text:";
        var prompt = $"{AiTextActions.Instruction(action)}\n\n{heading}\n{text}";
        var answer = await CompleteAsync(connection, AiTextActions.SystemPrompt, prompt, cancellationToken);
        return Clean(answer);
    }

    /// <summary>Prüft die Verbindung mit einer kleinen Anfrage und liefert die Antwort der KI.</summary>
    public async Task<string> TestAsync(AiConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var answer = await CompleteAsync(
            connection,
            "Du bist ein Verbindungstest. Antworte mit genau einem Wort.",
            "Antworte mit: OK",
            cancellationToken);

        return Clean(answer);
    }

    /// <summary>Eine Anfrage an den gewählten Anbieter; die Antwort wird je nach Bauart gelesen.</summary>
    public async Task<string> CompleteAsync(
        AiConnection connection,
        string system,
        string user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var request = BuildRequest(connection, system, user);
        HttpResponseMessage response;
        string body;

        try
        {
            response = await Client.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LiftUpException(
                "Die KI hat nicht innerhalb von zwei Minuten geantwortet. Bitte später erneut versuchen.");
        }
        catch (HttpRequestException exception)
        {
            throw new LiftUpException(
                $"Die KI ist nicht erreichbar ({connection.BaseUrl}). Bitte Adresse und Internetverbindung prüfen.\n\n"
                + exception.Message);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LiftUpException(DescribeError(response.StatusCode, body));
        }

        return ReadAnswer(connection.Provider.Protocol, body);
    }

    private static HttpRequestMessage BuildRequest(AiConnection connection, string system, string user)
    {
        var baseUrl = connection.BaseUrl.TrimEnd('/');
        var credential = connection.Secret.Trim();
        var model = connection.Model.Trim();

        switch (connection.Provider.Protocol)
        {
            case AiProtocol.Anthropic:
            {
                var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/messages");
                request.Headers.TryAddWithoutValidation("x-api-key", credential);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                request.Content = JsonContent(new
                {
                    model,
                    max_tokens = 8000,
                    system,
                    messages = new[] { new { role = "user", content = user } },
                });
                return request;
            }

            case AiProtocol.Google:
            {
                var url = $"{baseUrl}/models/{Uri.EscapeDataString(model)}:generateContent"
                          + $"?key={Uri.EscapeDataString(credential)}";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = JsonContent(new
                {
                    system_instruction = new { parts = new[] { new { text = system } } },
                    contents = new[]
                    {
                        new { role = "user", parts = new[] { new { text = user } } },
                    },
                    generationConfig = new { temperature = 0.3 },
                });
                return request;
            }

            default:
            {
                var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
                if (credential.Length > 0)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential);
                }

                request.Content = JsonContent(new
                {
                    model,
                    temperature = 0.3,
                    messages = new object[]
                    {
                        new { role = "system", content = system },
                        new { role = "user", content = user },
                    },
                });
                return request;
            }
        }
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>Liest die Antwort des Anbieters; die Bauarten unterscheiden sich nur darin.</summary>
    private static string ReadAnswer(AiProtocol protocol, string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            throw new LiftUpException("Die Antwort der KI war nicht lesbar.");
        }

        var text = protocol switch
        {
            AiProtocol.Anthropic => JoinParts(root?["content"]),
            AiProtocol.Google => JoinGoogle(root),
            _ => root?["choices"]?[0]?["message"]?["content"]?.GetValue<string>(),
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new LiftUpException("Die KI hat keinen Text zurückgegeben. Bitte Modell prüfen.");
        }

        return text;
    }

    private static string JoinParts(JsonNode? content)
    {
        if (content is not JsonArray parts)
        {
            return string.Empty;
        }

        return string.Concat(parts
            .Select(part => part?["text"]?.GetValue<string>())
            .Where(text => !string.IsNullOrEmpty(text)));
    }

    private static string JoinGoogle(JsonNode? root)
    {
        if (root?["candidates"]?[0]?["content"]?["parts"] is not JsonArray parts)
        {
            return string.Empty;
        }

        return string.Concat(parts
            .Select(part => part?["text"]?.GetValue<string>())
            .Where(text => !string.IsNullOrEmpty(text)));
    }

    /// <summary>
    /// Fehlermeldung des Anbieters in einem Satz: Status, Meldung und – wo möglich – ein
    /// Hinweis, was zu tun ist.
    /// </summary>
    private static string DescribeError(System.Net.HttpStatusCode status, string body)
    {
        var message = ExtractMessage(body);
        var hint = status switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Der Schlüssel wurde nicht angenommen.",
            System.Net.HttpStatusCode.Forbidden => "Der Schlüssel hat keine Berechtigung für dieses Modell.",
            System.Net.HttpStatusCode.TooManyRequests => "Zu viele Anfragen – bitte kurz warten.",
            System.Net.HttpStatusCode.NotFound => "Adresse oder Modell wurde nicht gefunden.",
            _ => status is >= (System.Net.HttpStatusCode)500
                ? "Der Anbieter meldet einen Fehler."
                : "Die Anfrage wurde abgelehnt.",
        };

        var detail = message.Length > 0 ? message : "ohne weitere Angaben";
        return $"{hint} ({(int)status}) {detail}";
    }

    /// <summary>Meldung aus der Antwort des Anbieters, egal in welcher Bauart.</summary>
    private static string ExtractMessage(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            var message = root?["error"]?["message"]?.GetValue<string>()
                          ?? root?["error"]?.GetValue<string>()
                          ?? root?["message"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(message))
            {
                return Shorten(message.Trim());
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Kein JSON: der Text selbst hilft weiter.
        }

        return Shorten(body.Trim());
    }

    private static string Shorten(string text) =>
        text.Length <= 300 ? text : text[..300].TrimEnd() + "…";

    /// <summary>Räumt die Antwort auf: Anführungszeichen und Leerzeilen am Rand fallen weg.</summary>
    private static string Clean(string text)
    {
        var cleaned = text.Replace("\r\n", "\n").Trim();

        if (cleaned.Length > 1 && cleaned[0] == '"' && cleaned[^1] == '"')
        {
            cleaned = cleaned[1..^1].Trim();
        }

        return cleaned;
    }
}
