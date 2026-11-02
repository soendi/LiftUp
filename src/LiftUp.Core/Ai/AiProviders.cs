namespace LiftUp.Core.Ai;

/// <summary>Art der Anfrage an einen KI-Anbieter.</summary>
public enum AiProtocol
{
    /// <summary>Wie OpenAI: <c>/chat/completions</c> mit „Authorization: Bearer“.</summary>
    OpenAi,

    /// <summary>Anthropic: <c>/messages</c> mit „x-api-key“.</summary>
    Anthropic,

    /// <summary>Google: <c>/models/…:generateContent</c> mit Schlüssel als Parameter.</summary>
    Google,
}

/// <summary>Womit sich LiftUp beim Anbieter anmeldet.</summary>
public enum AiAuthMode
{
    /// <summary>API-Schlüssel aus der Verwaltungsoberfläche des Anbieters.</summary>
    ApiKey,

    /// <summary>Zugangstoken einer OAuth2-Anmeldung, etwa aus der Konsole des Anbieters.</summary>
    Token,
}

/// <summary>
/// Ein KI-Anbieter samt Adresse, Modellen und Hinweisen zum Schlüssel. Die Adresse lässt
/// sich im Reiter „KI“ überschreiben, damit auch ein eigener Server (etwa Ollama im
/// Netzwerk) angesprochen werden kann.
/// </summary>
public sealed record AiProvider(
    string Key,
    string Name,
    AiProtocol Protocol,
    string BaseUrl,
    string DefaultModel,
    IReadOnlyList<string> Models,
    string KeyLabel,
    string KeyHint,
    string SetupUrl,
    bool NeedsKey = true);

public static class AiProviders
{
    /// <summary>Alle Anbieter in der Reihenfolge des Auswahlfelds; Groq steht oben.</summary>
    public static IReadOnlyList<AiProvider> All { get; } =
    [
        new(
            "groq",
            "Groq (kostenlos starten)",
            AiProtocol.OpenAi,
            "https://api.groq.com/openai/v1",
            "llama-3.3-70b-versatile",
            ["llama-3.3-70b-versatile", "llama-3.1-8b-instant", "openai/gpt-oss-120b", "openai/gpt-oss-20b"],
            "API-Schlüssel",
            "Beginnt mit „gsk_“. Kostenlos erhältlich unter console.groq.com → API Keys.",
            "https://console.groq.com/keys"),
        new(
            "openai",
            "OpenAI (ChatGPT)",
            AiProtocol.OpenAi,
            "https://api.openai.com/v1",
            "gpt-4o-mini",
            ["gpt-4o-mini", "gpt-4o", "gpt-4.1-mini", "gpt-4.1"],
            "API-Schlüssel",
            "Beginnt mit „sk-“. Anlegen unter platform.openai.com → API keys.",
            "https://platform.openai.com/api-keys"),
        new(
            "anthropic",
            "Anthropic (Claude)",
            AiProtocol.Anthropic,
            "https://api.anthropic.com/v1",
            "claude-3-5-sonnet-latest",
            ["claude-3-5-sonnet-latest", "claude-3-5-haiku-latest", "claude-sonnet-4-5", "claude-haiku-4-5"],
            "API-Schlüssel",
            "Beginnt mit „sk-ant-“. Anlegen unter console.anthropic.com → API keys.",
            "https://console.anthropic.com/settings/keys"),
        new(
            "google",
            "Google Gemini",
            AiProtocol.Google,
            "https://generativelanguage.googleapis.com/v1beta",
            "gemini-2.5-flash",
            ["gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.0-flash"],
            "API-Schlüssel",
            "Anlegen unter aistudio.google.com → Get API key.",
            "https://aistudio.google.com/app/apikey"),
        new(
            "mistral",
            "Mistral AI",
            AiProtocol.OpenAi,
            "https://api.mistral.ai/v1",
            "mistral-large-latest",
            ["mistral-large-latest", "mistral-small-latest", "open-mistral-nemo"],
            "API-Schlüssel",
            "Anlegen unter console.mistral.ai → API keys.",
            "https://console.mistral.ai/api-keys"),
        new(
            "openrouter",
            "OpenRouter (viele Modelle)",
            AiProtocol.OpenAi,
            "https://openrouter.ai/api/v1",
            "openai/gpt-4o-mini",
            ["openai/gpt-4o-mini", "anthropic/claude-3.5-sonnet", "meta-llama/llama-3.3-70b-instruct"],
            "API-Schlüssel",
            "Beginnt mit „sk-or-“. Anlegen unter openrouter.ai → Keys.",
            "https://openrouter.ai/keys"),
        new(
            "deepseek",
            "DeepSeek",
            AiProtocol.OpenAi,
            "https://api.deepseek.com/v1",
            "deepseek-chat",
            ["deepseek-chat", "deepseek-reasoner"],
            "API-Schlüssel",
            "Anlegen unter platform.deepseek.com → API keys.",
            "https://platform.deepseek.com/api_keys"),
        new(
            "ollama",
            "Ollama (eigener Rechner, ohne Schlüssel)",
            AiProtocol.OpenAi,
            "http://localhost:11434/v1",
            "llama3.1",
            ["llama3.1", "mistral", "qwen2.5"],
            "API-Schlüssel",
            "Ein lokales Modell braucht keinen Schlüssel. Läuft Ollama auf einem anderen Rechner, hier dessen Adresse eintragen.",
            "https://ollama.com/download",
            NeedsKey: false),
    ];

    /// <summary>Der Anbieter zum gespeicherten Schlüssel; ohne Treffer der erste (Groq).</summary>
    public static AiProvider Find(string? key) =>
        All.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
