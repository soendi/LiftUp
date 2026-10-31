using System.Globalization;
using System.Text;

namespace LiftUp.Core.Mail;

/// <summary>
/// Baut eine vollständige MIME-Nachricht. Gmail erwartet beim Versand über die
/// API genau dieses Format (RFC 822 als Base64url); Anhänge werden als
/// eigene MIME-Teile eingebettet.
/// </summary>
public static class MailMimeBuilder
{
    public static string BuildRaw(MailMessage message, string fromAddress)
    {
        ArgumentNullException.ThrowIfNull(message);

        var boundary = "liftup-" + Guid.NewGuid().ToString("N");
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(fromAddress))
        {
            builder.Append("From: ").Append(fromAddress.Trim()).Append("\r\n");
        }

        builder.Append("To: ").Append(message.To.Trim()).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(message.Cc))
        {
            builder.Append("Cc: ").Append(message.Cc.Trim()).Append("\r\n");
        }

        builder.Append("Subject: ").Append(EncodeHeader(message.Subject)).Append("\r\n");
        builder.Append("Date: ")
            .Append(DateTime.Now.ToString("r", CultureInfo.InvariantCulture))
            .Append("\r\n");
        builder.Append("MIME-Version: 1.0\r\n");
        builder.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\r\n\r\n");

        builder.Append("--").Append(boundary).Append("\r\n");
        builder.Append("Content-Type: text/html; charset=utf-8\r\n");
        builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        AppendBase64Lines(builder, Encoding.UTF8.GetBytes(message.BodyHtml ?? string.Empty));
        builder.Append("\r\n");

        var attachment = ReadAttachment(message.AttachmentPath);
        if (attachment is not null)
        {
            var (fileName, bytes) = attachment.Value;
            builder.Append("--").Append(boundary).Append("\r\n");
            builder.Append("Content-Type: application/pdf; name=\"")
                .Append(Uri.EscapeDataString(fileName))
                .Append("\"\r\n");
            builder.Append("Content-Disposition: attachment; filename=\"")
                .Append(Uri.EscapeDataString(fileName))
                .Append("\"\r\n");
            builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            AppendBase64Lines(builder, bytes);
            builder.Append("\r\n");
        }

        builder.Append("--").Append(boundary).Append("--\r\n");
        return builder.ToString();
    }

    public static string ToBase64Url(byte[] data) => Convert.ToBase64String(data)
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');

    /// <summary>Kodiert Sonderzeichen im Betreff nach RFC 2047.</summary>
    public static string EncodeHeader(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var isAscii = text.All(character => character is >= ' ' and <= '~');
        return isAscii ? text : "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "?=";
    }

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
            return null;
        }
    }

    private static void AppendBase64Lines(StringBuilder builder, byte[] bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        for (var index = 0; index < base64.Length; index += 76)
        {
            builder.Append(base64, index, Math.Min(76, base64.Length - index)).Append("\r\n");
        }
    }
}
