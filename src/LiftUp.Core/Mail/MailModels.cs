namespace LiftUp.Core.Mail;

/// <summary>Anbieter für den Versand aus dem eigenen Postfach.</summary>
public enum MailProvider
{
    Google = 0,
    Microsoft365 = 1,
}

public sealed record MailProviderInfo(
    MailProvider Value,
    string Name,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string Scope,
    string UserInfoEndpoint,
    bool SendScopeOnToken,
    bool UseLoopbackIpAddress);

public static class MailProviders
{
    public static IReadOnlyList<MailProviderInfo> All { get; } =
    [
        new(
            MailProvider.Google,
            "Google (Gmail)",
            "https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token",
            "openid email https://www.googleapis.com/auth/gmail.send",
            "https://openidconnect.googleapis.com/v1/userinfo",
            SendScopeOnToken: false,
            UseLoopbackIpAddress: true),
        new(
            MailProvider.Microsoft365,
            "Microsoft 365 / Outlook",
            "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
            "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            "offline_access openid email profile https://graph.microsoft.com/Mail.Send",
            "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName",
            SendScopeOnToken: true,
            UseLoopbackIpAddress: false),
    ];

    public static MailProviderInfo Get(MailProvider provider) =>
        All.First(info => info.Value == provider);
}

/// <summary>Eingerichtetes Postfach eines Benutzers.</summary>
public sealed class MailAccount
{
    public MailProvider Provider { get; set; } = MailProvider.Google;

    /// <summary>Client-ID der eigenen App-Registrierung (Google Cloud bzw. Azure).</summary>
    public string ClientId { get; set; } = string.Empty;

    public string EmailAddress { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>Eine zu versendende Nachricht samt optionalem PDF-Anhang.</summary>
public sealed class MailMessage
{
    public string To { get; set; } = string.Empty;
    public string? Cc { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    public string? AttachmentPath { get; set; }
}
