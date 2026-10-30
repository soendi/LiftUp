namespace LiftUp.Core.Documents;

/// <summary>Seitenformat für die PDF-Erzeugung.</summary>
public sealed record PdfPageSettings(string Format = "A4", bool Landscape = false, double MarginMm = 15);

/// <summary>
/// Wandelt fertiges HTML in ein PDF. Die Umsetzung liegt in der Oberflächenschicht
/// (WebView2), damit der Kern plattformunabhängig und testbar bleibt.
/// </summary>
public interface IHtmlToPdfRenderer
{
    Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default);
}
