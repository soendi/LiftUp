using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LiftUp.Core.Domain;
using LiftUp.Core.Mail;

namespace LiftUp.App.Mail;

/// <summary>
/// Öffnet die Anmeldung im Standardbrowser und nimmt die Antwort auf einer
/// lokalen Adresse entgegen. Der Browser zeigt dabei die Seite des Anbieters,
/// LiftUp sieht das Passwort nie.
/// </summary>
public static class MailOAuthLogin
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    public static async Task<MailAccount> ConnectAsync(
        AppServices services,
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        var info = MailProviders.Get(account.Provider);
        var port = GetFreePort();
        var host = info.UseLoopbackIpAddress ? "127.0.0.1" : "localhost";
        var redirectUri = $"http://{host}:{port}/";
        var (url, verifier, state) = MailAccountService.BuildAuthorizationRequest(account, redirectUri);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            (string? Code, string? Error, string? State) callback;
            try
            {
                callback = await WaitForCallbackAsync(listener, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw new ValidationException(
                    "Die Anmeldung wurde nicht innerhalb von fünf Minuten abgeschlossen. Bitte erneut starten.");
            }

            if (!string.IsNullOrEmpty(callback.Error))
            {
                throw new ValidationException($"Die Anmeldung wurde abgelehnt ({callback.Error}).");
            }

            if (string.IsNullOrEmpty(callback.Code))
            {
                throw new ValidationException("Die Anmeldung lieferte keinen Code.");
            }

            if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            {
                throw new ValidationException("Die Anmeldeantwort konnte nicht zugeordnet werden.");
            }

            return await services.Mail
                .CompleteLoginAsync(account, callback.Code!, verifier, redirectUri, cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<(string? Code, string? Error, string? State)> WaitForCallbackAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LoginTimeout);

        using var client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token).ConfigureAwait(true)))
        {
            // Kopfzeilen der Anfrage lesen, bis die Leerzeile kommt.
        }

        var target = requestLine?.Split(' ') is { Length: >= 2 } parts ? parts[1] : "/";
        var query = ParseQuery(target);
        var success = query.ContainsKey("code");

        await WritePageAsync(stream, success, timeout.Token).ConfigureAwait(true);

        return (
            query.TryGetValue("code", out var code) ? code : null,
            query.TryGetValue("error", out var error) ? error : null,
            query.TryGetValue("state", out var state) ? state : null);
    }

    private static async Task WritePageAsync(Stream stream, bool success, CancellationToken cancellationToken)
    {
        var message = success
            ? "Die Anmeldung ist abgeschlossen. Sie können dieses Fenster schließen und zu LiftUp zurückkehren."
            : "Die Anmeldung wurde nicht abgeschlossen. Bitte in LiftUp erneut starten.";

        var body = $"""
            <!DOCTYPE html>
            <html lang="de">
            <head><meta charset="utf-8" /><title>LiftUp</title></head>
            <body style="font-family:'Segoe UI',Arial,sans-serif;padding:40px;color:#1F2A44;">
              <h1 style="font-size:18px;">LiftUp</h1>
              <p>{message}</p>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(true);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(true);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(true);
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var start = target.IndexOf('?');
        if (start < 0)
        {
            return result;
        }

        foreach (var pair in target[(start + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}
