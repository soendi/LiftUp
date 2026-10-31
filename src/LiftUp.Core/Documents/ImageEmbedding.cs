using System.Globalization;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Documents;

/// <summary>
/// Bettet Bilder als Data-URI ein. Das funktioniert unabhängig von Pfaden und
/// Laufwerksbuchstaben und damit auch, wenn der Projektordner auf einem Netzlaufwerk liegt.
/// </summary>
public static class ImageEmbedding
{
    /// <summary>Dateiendungen, die die Chromium-Engine in WebView2 darstellen kann.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"];

    /// <summary>Endungen, die gespeichert, aber von Chromium nicht gerendert werden.</summary>
    public static IReadOnlyList<string> StoredOnlyExtensions { get; } = [".tif", ".tiff"];

    public static string? ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".tif" or ".tiff" => "image/tiff",
            _ => null,
        };

    public static string? ToImageTag(LibraryImage image, string? style = null) =>
        ToImageTag(image.FilePath, image.ContentType, image.Name, style);

    /// <summary>
    /// Bettet eine Bilddatei anhand ihres Pfades ein; der Inhaltstyp wird aus der
    /// Dateiendung abgeleitet.
    /// </summary>
    public static string? ToImageTag(string filePath, string name, string? style = null)
    {
        var contentType = ContentTypeFor(filePath);
        return contentType is null ? null : ToImageTag(filePath, contentType, name, style);
    }

    private static string? ToImageTag(string filePath, string contentType, string name, string? style)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var css = string.IsNullOrWhiteSpace(style) ? "max-width:100%;" : style;
        var alt = EscapeAttribute(name);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"<img src=\"data:{contentType};base64,{Convert.ToBase64String(bytes)}\" alt=\"{alt}\" style=\"{css}\" />");
    }

    private static string EscapeAttribute(string value) => value
        .Replace("&", "&amp;")
        .Replace("\"", "&quot;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
