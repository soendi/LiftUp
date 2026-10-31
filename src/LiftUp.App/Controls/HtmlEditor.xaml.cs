using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace LiftUp.App.Controls;

/// <summary>
/// Einfacher WYSIWYG-Editor für Textbausteine: der Inhalt wird wie in einem
/// Textprogramm bearbeitet, gespeichert und gelesen wird aber HTML.
/// Fehlt die WebView2-Laufzeit, steht ein reines HTML-Feld als Rückfall bereit.
/// </summary>
public partial class HtmlEditor : UserControl
{
    private const string EditorPage = """
        <!DOCTYPE html>
        <html lang="de">
        <head>
        <meta charset="utf-8" />
        <style>
            html { height: 100%; }
            body {
                margin: 0;
                padding: 10px 12px;
                font-family: "Segoe UI", Arial, sans-serif;
                font-size: 14px;
                line-height: 1.45;
                color: #1F2A44;
                background: #FFFFFF;
                outline: none;
                box-sizing: border-box;
                min-height: 100%;
            }
            body:empty::before {
                content: "Text des Bausteins …";
                color: #9AA3AD;
            }
            p { margin: 0 0 8px 0; }
            ul, ol { margin: 0 0 8px 0; padding-left: 26px; }
            h1, h2, h3 { margin: 0 0 8px 0; }
            table { border-collapse: collapse; }
            td, th { border: 1px solid #DCE1E8; padding: 4px 6px; }
            a { color: #2D6CDF; }
            img { max-width: 100%; }
        </style>
        </head>
        <body contenteditable="true" spellcheck="false"></body>
        <script>
            window.liftupRange = null;
            document.addEventListener('selectionchange', function () {
                var selection = window.getSelection();
                if (selection && selection.rangeCount > 0) {
                    window.liftupRange = selection.getRangeAt(0).cloneRange();
                }
            });
            window.liftupExec = function (command, value) {
                document.body.focus();
                if (window.liftupRange) {
                    var selection = window.getSelection();
                    selection.removeAllRanges();
                    selection.addRange(window.liftupRange);
                }
                document.execCommand(command, false, value === undefined ? null : value);
            };
        </script>
        </html>
        """;

    private bool _ready;
    private bool _failed;
    private Task? _initialization;
    private string _pendingHtml = string.Empty;

    public HtmlEditor()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Zeigt den übergebenen HTML-Inhalt im Editor an.</summary>
    public void SetHtml(string? html)
    {
        _pendingHtml = html ?? string.Empty;

        if (_failed)
        {
            FallbackBox.Text = _pendingHtml;
            return;
        }

        if (_ready)
        {
            _ = ApplyHtmlAsync();
        }
    }

    /// <summary>Liefert den bearbeiteten Inhalt als HTML.</summary>
    public async Task<string> GetHtmlAsync()
    {
        if (_failed)
        {
            return FallbackBox.Text;
        }

        if (!_ready || Browser.CoreWebView2 is null)
        {
            return _pendingHtml;
        }

        try
        {
            var json = await Browser.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML");
            var html = Normalize(JsonSerializer.Deserialize<string>(json));
            _pendingHtml = html;
            return html;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Der Inhalt des Editors konnte nicht gelesen werden.", exception);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialization is not null)
        {
            return;
        }

        _initialization = InitializeAsync();
        await _initialization;
    }

    private async Task InitializeAsync()
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
                loaded.TrySetResult(args.IsSuccess);

            Browser.NavigationCompleted += OnNavigationCompleted;
            try
            {
                Browser.NavigateToString(EditorPage);
                if (!await loaded.Task.ConfigureAwait(true))
                {
                    throw new InvalidOperationException("Der Editor konnte nicht geladen werden.");
                }
            }
            finally
            {
                Browser.NavigationCompleted -= OnNavigationCompleted;
            }

            // Absätze statt <div>-Zeilen, damit der erzeugte HTML-Code ruhig bleibt.
            await Browser.CoreWebView2.ExecuteScriptAsync(
                "document.execCommand('defaultParagraphSeparator', false, 'p');");

            _ready = true;
            await ApplyHtmlAsync();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or WebView2RuntimeNotFoundException
                or System.Runtime.InteropServices.COMException
                or DllNotFoundException)
        {
            ShowFallback();
        }
    }

    private async Task ApplyHtmlAsync()
    {
        if (!_ready || Browser.CoreWebView2 is null)
        {
            return;
        }

        var script = "document.body.innerHTML = "
                     + JsonSerializer.Serialize(_pendingHtml)
                     + "; true";

        await Browser.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void ShowFallback()
    {
        _failed = true;
        _ready = false;

        Browser.Visibility = Visibility.Collapsed;
        ToolbarPanel.IsEnabled = false;
        FallbackBox.Text = _pendingHtml;
        FallbackBox.Visibility = Visibility.Visible;
    }

    private async void OnBoldClick(object sender, RoutedEventArgs e) => await ExecuteAsync("bold");

    private async void OnItalicClick(object sender, RoutedEventArgs e) => await ExecuteAsync("italic");

    private async void OnUnderlineClick(object sender, RoutedEventArgs e) => await ExecuteAsync("underline");

    private async void OnBulletListClick(object sender, RoutedEventArgs e) => await ExecuteAsync("insertUnorderedList");

    private async void OnNumberedListClick(object sender, RoutedEventArgs e) => await ExecuteAsync("insertOrderedList");

    private async void OnClearFormatClick(object sender, RoutedEventArgs e) => await ExecuteAsync("removeFormat");

    /// <summary>Fügt HTML an der Stelle der letzten Eingabe ein (Textbaustein, Bild).</summary>
    public async Task InsertHtmlAsync(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return;
        }

        if (_failed)
        {
            FallbackBox.Text += html;
            return;
        }

        if (!_ready || Browser.CoreWebView2 is null)
        {
            _pendingHtml += html;
            return;
        }

        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(
                "window.liftupExec('insertHTML', " + JsonSerializer.Serialize(html) + "); true");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Der Baustein konnte nicht eingefügt werden.", exception);
        }
    }

    private async Task ExecuteAsync(string command)
    {
        if (!_ready || Browser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync($"window.liftupExec('{command}'); true");
        }
        catch (Exception)
        {
            // Formatierung ist eine Bedienhilfe; ein Fehlschlag darf die Eingabe nicht stören.
        }
    }

    /// <summary>Chromium liefert für ein leeres Feld Platzhalter wie <c>&lt;br&gt;</c>.</summary>
    private static string Normalize(string? html)
    {
        var value = (html ?? string.Empty).Trim();
        return value switch
        {
            "" => string.Empty,
            "<br>" or "<div><br></div>" or "<p><br></p>" => string.Empty,
            _ => value,
        };
    }
}
