using System.IO;
using System.Windows;
using LiftUp.Core.Documents;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LiftUp.App.Documents;

/// <summary>
/// Rendert HTML über die auf Windows vorinstallierte WebView2-Engine nach PDF.
/// Läuft in einem unsichtbaren Fenster, damit die Oberfläche bedienbar bleibt.
/// </summary>
public sealed class WebView2PdfRenderer : IHtmlToPdfRenderer
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

    public async Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default)
    {
        var application = System.Windows.Application.Current
                          ?? throw new InvalidOperationException("Ohne laufende Anwendung kann WebView2 nicht genutzt werden.");

        var operation = application.Dispatcher.InvokeAsync(
            () => RenderCoreAsync(html, settings, cancellationToken));

        return await operation.Task.Unwrap();
    }

    private static async Task<byte[]> RenderCoreAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken)
    {
        var htmlPath = Path.Combine(Path.GetTempPath(), $"liftup-{Guid.NewGuid():N}.html");
        var pdfPath = Path.Combine(Path.GetTempPath(), $"liftup-{Guid.NewGuid():N}.pdf");
        await File.WriteAllTextAsync(htmlPath, html, cancellationToken).ConfigureAwait(true);

        var window = new Window
        {
            Width = 1000,
            Height = 1400,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
        };

        var webView = new WebView2();
        window.Content = webView;

        try
        {
            window.Show();
            await webView.EnsureCoreWebView2Async();

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
                loaded.TrySetResult(args.IsSuccess);

            webView.NavigationCompleted += OnNavigationCompleted;
            try
            {
                webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                var finished = await Task.WhenAny(loaded.Task, Task.Delay(LoadTimeout, cancellationToken))
                    .ConfigureAwait(true);

                if (finished != loaded.Task || !loaded.Task.Result)
                {
                    throw new InvalidOperationException("Die Vorlage konnte nicht geladen werden.");
                }
            }
            finally
            {
                webView.NavigationCompleted -= OnNavigationCompleted;
            }

            var print = webView.CoreWebView2.Environment.CreatePrintSettings();
            ApplyPageSettings(print, settings);

            var success = await webView.CoreWebView2.PrintToPdfAsync(pdfPath, print);
            if (!success || !File.Exists(pdfPath))
            {
                throw new InvalidOperationException("Das PDF konnte nicht erzeugt werden.");
            }

            return await File.ReadAllBytesAsync(pdfPath, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            window.Close();
            TryDelete(htmlPath);
            TryDelete(pdfPath);
        }
    }

    private static void ApplyPageSettings(CoreWebView2PrintSettings print, PdfPageSettings settings)
    {
        var (widthMm, heightMm) = settings.Format.ToUpperInvariant() switch
        {
            "A3" => (297.0, 420.0),
            _ => (210.0, 297.0),
        };

        if (settings.Landscape)
        {
            (widthMm, heightMm) = (heightMm, widthMm);
        }

        const double mmPerInch = 25.4;
        print.PageWidth = widthMm / mmPerInch;
        print.PageHeight = heightMm / mmPerInch;

        var margin = Math.Max(settings.MarginMm, 0) / mmPerInch;
        print.MarginTop = margin;
        print.MarginBottom = margin;
        print.MarginLeft = margin;
        print.MarginRight = margin;

        print.ShouldPrintBackgrounds = true;
        print.ShouldPrintHeaderAndFooter = false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temporäre Dateien sind unkritisch; Windows räumt sie später auf.
        }
    }
}
