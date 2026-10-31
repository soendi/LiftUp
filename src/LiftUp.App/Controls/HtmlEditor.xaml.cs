using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Documents;
using Microsoft.Web.WebView2.Core;

namespace LiftUp.App.Controls;

/// <summary>
/// Einfacher WYSIWYG-Editor für Textbausteine: der Inhalt wird wie in einem
/// Textprogramm bearbeitet, gespeichert und gelesen wird aber HTML.
/// Fehlt die WebView2-Laufzeit, steht ein reines HTML-Feld als Rückfall bereit.
/// Optional schlägt der Editor nach einem „/“ passende Textbausteine direkt am
/// Cursor vor.
/// </summary>
public partial class HtmlEditor : UserControl
{
    /// <summary>Ein Treffer der „/“-Suche: Name, Zusatzinfo und einzufügender HTML-Inhalt.</summary>
    public sealed record SnippetChoice(string Name, string Detail, string Html);

    /// <summary>
    /// Ein Treffer der „&amp;“-Suche: wie <see cref="SnippetChoice"/>, zusätzlich mit einem
    /// kleinen Vorschaubild als Data-URI.
    /// </summary>
    public sealed record ImageChoice(string Name, string Detail, string Html, string Preview);

    /// <summary>Ein angeklicktes Bild mit seinen aktuellen Layout-Angaben.</summary>
    public sealed record ImageFormatRequest(string Id, ImageFormat Current);

    /// <summary>
    /// Wird gesetzt, wenn im Text ein Bild angeklickt wurde. Liefert die neuen
    /// Angaben oder <c>null</c>, wenn nichts geändert werden soll.
    /// </summary>
    public Func<ImageFormatRequest, ImageFormat?>? ImageFormatRequested { get; set; }

    private const string EditorPageTemplate = """
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
            .liftup-snippets {
                position: absolute;
                z-index: 40;
                min-width: 250px;
                max-width: 420px;
                padding: 4px;
                background: #FFFFFF;
                border: 1px solid #C7D0DD;
                border-radius: 6px;
                box-shadow: 0 8px 20px rgba(31, 42, 68, 0.18);
                font-size: 13px;
                line-height: 1.3;
            }
            .liftup-snippets-row {
                display: flex;
                gap: 10px;
                align-items: center;
                padding: 5px 8px;
                border-radius: 4px;
                cursor: pointer;
            }
            .liftup-snippets-row.active { background: #E8F0FE; }
            .liftup-snippets-name { color: #1F2A44; white-space: nowrap; }
            .liftup-snippets-detail {
                color: #6B7686;
                font-size: 12px;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            .liftup-snippets-empty { padding: 6px 8px; color: #6B7686; }
            .liftup-snippets-thumb {
                width: 44px;
                height: 32px;
                object-fit: contain;
                background: #FFFFFF;
                border: 1px solid #DCE1E8;
                border-radius: 3px;
                flex: 0 0 auto;
            }
            /* Bilder: anklicken und mit der Maus verschieben */
            .liftup-fig { cursor: grab; -webkit-user-select: none; user-select: none; }
            .liftup-fig img { -webkit-user-drag: none; }
            .liftup-fig-dragging { opacity: 0.55; }
            body.liftup-dragging { cursor: grabbing; }
            #liftup-drop-line {
                position: absolute;
                left: 12px;
                right: 12px;
                height: 3px;
                background: #2D6CDF;
                border-radius: 2px;
                pointer-events: none;
                z-index: 60;
                display: none;
            }
            /* liftup-bildstil */
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

            // ---- Vorschläge für Textbausteine („/Text“) ----
            window.liftupSnippetEnabled = false;
            window.liftupSnippetPanel = null;
            window.liftupSnippetItems = [];
            window.liftupSnippetIndex = 0;
            window.liftupSnippetToken = null;
            window.liftupSnippetKind = null;
            window.liftupSnippetRequest = 0;

            window.liftupEnableSnippets = function (on) {
                window.liftupSnippetEnabled = !!on;
                if (!on) {
                    window.liftupCloseSnippets();
                }
            };

            window.liftupRemoveSnippetPanel = function () {
                var panel = window.liftupSnippetPanel;
                if (panel && panel.parentNode) {
                    panel.parentNode.removeChild(panel);
                }
                window.liftupSnippetPanel = null;
            };

            window.liftupCloseSnippets = function () {
                window.liftupRemoveSnippetPanel();
                window.liftupSnippetItems = [];
                window.liftupSnippetIndex = 0;
                window.liftupSnippetToken = null;
                window.liftupSnippetKind = null;
            };

            window.liftupSnippetTokenAtCaret = function () {
                var selection = window.getSelection();
                if (!selection || selection.rangeCount === 0 || !selection.isCollapsed) {
                    return null;
                }
                var node = selection.anchorNode;
                if (!node || node.nodeType !== 3) {
                    return null;
                }
                var offset = selection.anchorOffset;
                var before = node.nodeValue.substring(0, offset);

                // „&&bild“ sucht im aktiven Projekt plus globaler Bibliothek, „&bild“ nur im Projekt.
                var image = /(^|[\s\u00a0>])(&{1,2})([^\s&]{0,40})$/.exec(before);
                if (image) {
                    var prefix = image[2].length;
                    return {
                        kind: 'image',
                        all: prefix === 2,
                        node: node,
                        start: offset - prefix - image[3].length,
                        length: prefix + image[3].length,
                        query: image[3]
                    };
                }

                var snippet = /(^|[\s\u00a0>])\/([^\s\/]{0,40})$/.exec(before);
                if (!snippet) {
                    return null;
                }

                return {
                    kind: 'snippet',
                    all: false,
                    node: node,
                    start: offset - snippet[2].length - 1,
                    length: snippet[2].length + 1,
                    query: snippet[2]
                };
            };

            window.liftupCaretRect = function () {
                var selection = window.getSelection();
                if (!selection || selection.rangeCount === 0) {
                    return null;
                }
                var range = selection.getRangeAt(0).cloneRange();
                var rect = range.getBoundingClientRect();
                if (rect && (rect.width > 0 || rect.height > 0)) {
                    return rect;
                }
                var marker = document.createElement('span');
                marker.textContent = '\u200b';
                range.insertNode(marker);
                rect = marker.getBoundingClientRect();
                if (marker.parentNode) {
                    marker.parentNode.removeChild(marker);
                }
                return rect;
            };

            window.liftupRenderSnippets = function () {
                window.liftupRemoveSnippetPanel();
                var rect = window.liftupCaretRect();
                if (!rect) {
                    return;
                }

                var panel = document.createElement('div');
                panel.className = 'liftup-snippets';
                panel.setAttribute('contenteditable', 'false');

                var items = window.liftupSnippetItems;
                if (items.length === 0) {
                    var empty = document.createElement('div');
                    empty.className = 'liftup-snippets-empty';
                    empty.textContent = window.liftupSnippetKind === 'image'
                        ? 'Keine Bilder gefunden'
                        : 'Keine Textbausteine gefunden';
                    panel.appendChild(empty);
                } else {
                    items.forEach(function (item, position) {
                        var row = document.createElement('div');
                        row.className = 'liftup-snippets-row' + (position === window.liftupSnippetIndex ? ' active' : '');

                        if (item.preview) {
                            var thumb = document.createElement('img');
                            thumb.className = 'liftup-snippets-thumb';
                            thumb.src = item.preview;
                            thumb.alt = '';
                            row.appendChild(thumb);
                        }

                        var name = document.createElement('span');
                        name.className = 'liftup-snippets-name';
                        name.textContent = item.name;

                        var detail = document.createElement('span');
                        detail.className = 'liftup-snippets-detail';
                        detail.textContent = item.detail || '';

                        row.appendChild(name);
                        row.appendChild(detail);
                        row.addEventListener('mousedown', function (event) {
                            event.preventDefault();
                            window.liftupInsertSnippet(item);
                        });
                        panel.appendChild(row);
                    });
                }

                panel.style.top = (rect.bottom + window.scrollY + 2) + 'px';
                panel.style.left = Math.max(6, rect.left + window.scrollX) + 'px';
                document.body.appendChild(panel);
                window.liftupSnippetPanel = panel;
            };

            window.liftupSnippetQuery = function () {
                if (!window.liftupSnippetEnabled) {
                    return;
                }
                var token = window.liftupSnippetTokenAtCaret();
                if (!token) {
                    window.liftupCloseSnippets();
                    return;
                }
                window.liftupSnippetToken = token;
                window.liftupSnippetKind = token.kind;
                var requestId = ++window.liftupSnippetRequest;
                try {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: token.kind === 'image' ? 'imageQuery' : 'snippetQuery',
                        requestId: requestId,
                        query: token.query,
                        all: !!token.all
                    }));
                } catch (error) {
                    window.liftupCloseSnippets();
                }
            };

            window.liftupSnippetResults = function (requestId, matches) {
                if (!window.liftupSnippetEnabled || requestId !== window.liftupSnippetRequest) {
                    return;
                }
                if (!window.liftupSnippetToken) {
                    return;
                }
                window.liftupSnippetItems = Array.isArray(matches) ? matches : [];
                window.liftupSnippetIndex = 0;
                window.liftupRenderSnippets();
            };

            window.liftupMoveSnippet = function (step) {
                var count = window.liftupSnippetItems.length;
                if (count === 0) {
                    return;
                }
                window.liftupSnippetIndex = (window.liftupSnippetIndex + step + count) % count;
                window.liftupRenderSnippets();
            };

            window.liftupInsertSnippet = function (item) {
                var token = window.liftupSnippetToken;
                var selection = window.getSelection();
                window.liftupCloseSnippets();
                if (!item || !token || !selection || selection.rangeCount === 0) {
                    return;
                }
                if (!token.node || !token.node.isConnected) {
                    return;
                }

                var range = document.createRange();
                var end = token.start + token.length;
                try {
                    range.setStart(token.node, token.start);
                    range.setEnd(token.node, Math.min(end, token.node.nodeValue.length));
                } catch (error) {
                    return;
                }

                range.deleteContents();
                selection.removeAllRanges();
                selection.addRange(range);
                document.execCommand('insertHTML', false, item.html || '');
            };

            document.addEventListener('keydown', function (event) {
                if (!window.liftupSnippetEnabled || !window.liftupSnippetPanel) {
                    return;
                }
                if (event.key === 'ArrowDown') {
                    event.preventDefault();
                    window.liftupMoveSnippet(1);
                } else if (event.key === 'ArrowUp') {
                    event.preventDefault();
                    window.liftupMoveSnippet(-1);
                } else if (event.key === 'Enter') {
                    event.preventDefault();
                    if (window.liftupSnippetItems.length > 0) {
                        window.liftupInsertSnippet(window.liftupSnippetItems[window.liftupSnippetIndex]);
                    } else {
                        window.liftupCloseSnippets();
                    }
                } else if (event.key === 'Escape') {
                    event.preventDefault();
                    window.liftupCloseSnippets();
                }
            }, true);

            document.addEventListener('input', function () {
                window.liftupSnippetQuery();
            });

            window.liftupGetSelectionHtml = function () {
                var selection = window.getSelection();
                if (!selection || selection.rangeCount === 0 || selection.isCollapsed) {
                    return null;
                }
                var holder = document.createElement('div');
                holder.appendChild(selection.getRangeAt(0).cloneContents());
                return holder.innerHTML;
            };

            // ---- Bild anklicken: Größe, Seite, Textfluss und Unterschrift ändern ----
            window.liftupFigCounter = 0;

            window.liftupFigId = function (figure) {
                if (!figure.dataset.liftupId) {
                    window.liftupFigCounter += 1;
                    figure.dataset.liftupId = 'fig' + window.liftupFigCounter;
                }
                return figure.dataset.liftupId;
            };

            window.liftupOpenFormat = function (figure) {
                var caption = figure.querySelector('figcaption');
                try {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: 'imageFormat',
                        requestId: 0,
                        id: window.liftupFigId(figure),
                        size: figure.dataset.liftupSize || 'medium',
                        place: figure.dataset.liftupPlace || 'center',
                        flow: figure.dataset.liftupFlow || 'inline',
                        caption: caption ? caption.textContent : ''
                    }));
                } catch (error) {
                    // Ohne Verbindung zur Anwendung bleibt das Bild unverändert.
                }
            };

            window.liftupFigureClasses = function (figure, place) {
                var size = figure.dataset.liftupSize || 'medium';
                var flow = figure.dataset.liftupFlow || 'inline';
                figure.dataset.liftupPlace = place;
                figure.className = 'liftup-fig liftup-fig-' + size + ' liftup-fig-' + place + ' liftup-fig-' + flow;
            };

            // Blöcke sind die Elemente direkt unter <body>; dazwischen wird das Bild abgelegt.
            window.liftupFindBlock = function (element) {
                while (element && element !== document.body) {
                    if (element.parentNode === document.body) {
                        return element;
                    }
                    element = element.parentNode;
                }
                return null;
            };

            window.liftupDropLine = function () {
                var line = document.getElementById('liftup-drop-line');
                if (!line) {
                    line = document.createElement('div');
                    line.id = 'liftup-drop-line';
                    document.body.appendChild(line);
                }
                return line;
            };

            window.liftupDropTarget = function (x, y) {
                var element = document.elementFromPoint(x, y);
                if (!element) {
                    return null;
                }

                var strip = document.getElementById('liftup-below');
                if (strip && strip.contains(element)) {
                    return { strip: strip, y: strip.getBoundingClientRect().top };
                }

                var block = window.liftupFindBlock(element);
                if (!block) {
                    return null;
                }

                var rect = block.getBoundingClientRect();
                var after = y > rect.top + rect.height / 2;
                return { block: block, after: after, y: after ? rect.bottom : rect.top };
            };

            window.liftupShowDropLine = function (target) {
                var line = window.liftupDropLine();
                if (!target) {
                    line.style.display = 'none';
                    return;
                }
                line.style.display = 'block';
                line.style.top = (target.y + window.scrollY - 2) + 'px';
            };

            window.liftupDrag = null;

            document.addEventListener('pointerdown', function (event) {
                if (event.button !== 0 || !event.target || !event.target.closest) {
                    return;
                }
                var figure = event.target.closest('figure.liftup-fig');
                if (!figure) {
                    return;
                }

                window.liftupDrag = {
                    figure: figure,
                    startX: event.clientX,
                    startY: event.clientY,
                    moved: false
                };
                document.addEventListener('pointermove', window.liftupPointerMove, true);
                document.addEventListener('pointerup', window.liftupPointerUp, true);
            });

            window.liftupPointerMove = function (event) {
                var drag = window.liftupDrag;
                if (!drag) {
                    return;
                }

                if (!drag.moved) {
                    if (Math.abs(event.clientX - drag.startX) < 6 && Math.abs(event.clientY - drag.startY) < 6) {
                        return;
                    }
                    drag.moved = true;
                    document.body.classList.add('liftup-dragging');
                    drag.figure.classList.add('liftup-fig-dragging');
                    drag.figure.style.pointerEvents = 'none';
                }

                event.preventDefault();
                window.liftupShowDropLine(window.liftupDropTarget(event.clientX, event.clientY));
            };

            window.liftupPointerUp = function (event) {
                var drag = window.liftupDrag;
                if (!drag) {
                    return;
                }

                document.removeEventListener('pointermove', window.liftupPointerMove, true);
                document.removeEventListener('pointerup', window.liftupPointerUp, true);
                window.liftupDrag = null;

                // Ohne Bewegung war es ein Klick: Größe, Seite, Textfluss, Unterschrift.
                if (!drag.moved) {
                    window.liftupOpenFormat(drag.figure);
                    return;
                }

                drag.figure.classList.remove('liftup-fig-dragging');
                drag.figure.style.pointerEvents = '';
                document.body.classList.remove('liftup-dragging');
                window.liftupShowDropLine(null);
                window.liftupMoveFigure(drag.figure, event.clientX, event.clientY);
            };

            window.liftupMoveFigure = function (figure, x, y) {
                var id = window.liftupFigId(figure);
                var anchor = document.querySelector('[data-liftup-anchor="' + id + '"]');
                var target = window.liftupDropTarget(x, y);
                var strip = document.getElementById('liftup-below');

                if (target && target.strip) {
                    // In den Streifen „unter dem Text“ gezogen.
                    if (!anchor && figure.parentNode) {
                        anchor = document.createElement('span');
                        anchor.setAttribute('data-liftup-anchor', id);
                        figure.parentNode.insertBefore(anchor, figure);
                    }
                    if (figure.dataset.liftupPlace !== 'below') {
                        window.liftupFigureClasses(figure, 'below');
                    }
                    target.strip.appendChild(figure);
                } else if (target && target.block && target.block !== figure) {
                    // Aus dem Streifen herausgezogen: das Bild steht wieder im Text.
                    if (figure.dataset.liftupPlace === 'below') {
                        window.liftupFigureClasses(figure, 'center');
                        if (anchor && anchor.parentNode) {
                            anchor.parentNode.removeChild(anchor);
                        }
                    }
                    if (target.after) {
                        target.block.parentNode.insertBefore(figure, target.block.nextSibling);
                    } else {
                        target.block.parentNode.insertBefore(figure, target.block);
                    }
                } else if (!target) {
                    document.body.appendChild(figure);
                }

                if (strip && strip.children.length === 0 && strip.parentNode) {
                    strip.parentNode.removeChild(strip);
                }
            };

            window.liftupBelowStrip = function () {
                var strip = document.getElementById('liftup-below');
                if (!strip) {
                    strip = document.createElement('div');
                    strip.id = 'liftup-below';
                    strip.className = 'liftup-fig-strip';
                    document.body.appendChild(strip);
                }
                return strip;
            };

            window.liftupApplyImageFormat = function (value) {
                if (!value || !value.id) {
                    return false;
                }
                var figure = document.querySelector('[data-liftup-id="' + value.id + '"]');
                if (!figure) {
                    return false;
                }

                figure.className = 'liftup-fig liftup-fig-' + value.size
                    + ' liftup-fig-' + value.place + ' liftup-fig-' + value.flow;
                figure.dataset.liftupSize = value.size;
                figure.dataset.liftupPlace = value.place;
                figure.dataset.liftupFlow = value.flow;

                var caption = figure.querySelector('figcaption');
                if (value.caption) {
                    if (!caption) {
                        caption = document.createElement('figcaption');
                        figure.appendChild(caption);
                    }
                    caption.textContent = value.caption;
                } else if (caption && caption.parentNode) {
                    caption.parentNode.removeChild(caption);
                }

                // „Unter dem Text“: das Bild wandert in den Streifen am Ende.
                var anchor = document.querySelector('[data-liftup-anchor="' + value.id + '"]');
                if (value.place === 'below') {
                    if (!anchor) {
                        anchor = document.createElement('span');
                        anchor.setAttribute('data-liftup-anchor', value.id);
                        if (figure.parentNode) {
                            figure.parentNode.insertBefore(anchor, figure);
                        }
                    }
                    window.liftupBelowStrip().appendChild(figure);
                } else if (anchor && anchor.parentNode) {
                    anchor.parentNode.replaceChild(figure, anchor);
                }

                return true;
            };

            window.liftupGetHtml = function () {
                window.liftupCloseSnippets();
                return document.body.innerHTML;
            };
        </script>
        </html>
        """;

    private static readonly string EditorPage =
        EditorPageTemplate.Replace("/* liftup-bildstil */", ImageFormat.Css, StringComparison.Ordinal);

    private bool _ready;
    private bool _failed;
    private Task? _initialization;
    private string _pendingHtml = string.Empty;
    private Func<string, IReadOnlyList<SnippetChoice>>? _snippetSearch;
    private Func<string, bool, IReadOnlyList<ImageChoice>>? _imageSearch;

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
            var json = await Browser.CoreWebView2.ExecuteScriptAsync(
                "window.liftupGetHtml ? window.liftupGetHtml() : document.body.innerHTML");
            var html = Normalize(JsonSerializer.Deserialize<string>(json));
            _pendingHtml = html;
            return html;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Der Inhalt des Editors konnte nicht gelesen werden.", exception);
        }
    }

    /// <summary>Liefert die markierte Stelle als HTML; <c>null</c>, wenn nichts markiert ist.</summary>
    public async Task<string?> GetSelectionHtmlAsync()
    {
        if (_failed || !_ready || Browser.CoreWebView2 is null)
        {
            return null;
        }

        try
        {
            var json = await Browser.CoreWebView2.ExecuteScriptAsync(
                "window.liftupGetSelectionHtml ? window.liftupGetSelectionHtml() : null");
            var html = JsonSerializer.Deserialize<string?>(json);
            return string.IsNullOrWhiteSpace(html) ? null : html;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Die Auswahl konnte nicht gelesen werden.", exception);
        }
    }

    /// <summary>
    /// Schaltet die Vorschläge ein: sobald im Text ein „/“ getippt wird, sucht der
    /// Editor über <paramref name="search"/> nach passenden Textbausteinen und
    /// zeigt sie am Cursor an. Auswahl mit Pfeiltasten, Einfügen mit Enter.
    /// </summary>
    public void EnableSnippetAutocomplete(Func<string, IReadOnlyList<SnippetChoice>> search)
    {
        _snippetSearch = search;

        if (_ready && Browser.CoreWebView2 is not null)
        {
            _ = EnableSnippetsAsync();
        }
    }

    /// <summary>
    /// Schaltet die Bildsuche ein: nach „&amp;“ sucht der Editor nur im aktiven Projekt, nach
    /// „&amp;&amp;“ zusätzlich in der globalen Bibliothek – in anderen Projekten nie.
    /// Jeder Treffer zeigt ein Vorschaubild.
    /// </summary>
    public void EnableImageAutocomplete(Func<string, bool, IReadOnlyList<ImageChoice>> search)
    {
        _imageSearch = search;

        if (_ready && Browser.CoreWebView2 is not null)
        {
            _ = EnableSnippetsAsync();
        }
    }

    private async Task EnableSnippetsAsync()
    {
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync("window.liftupEnableSnippets(true); true");
        }
        catch (Exception)
        {
            // Die Vorschläge sind eine Bedienhilfe; ohne sie bleibt der Editor nutzbar.
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

            Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // Absätze statt <div>-Zeilen, damit der erzeugte HTML-Code ruhig bleibt.
            await Browser.CoreWebView2.ExecuteScriptAsync(
                "document.execCommand('defaultParagraphSeparator', false, 'p');");

            _ready = true;
            await ApplyHtmlAsync();

            if (_snippetSearch is not null || _imageSearch is not null)
            {
                await EnableSnippetsAsync();
            }
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

    /// <summary>Beantwortet die „/“- und die „&amp;“-Suche aus dem Editor.</summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var payload = WebMessagePayload.Unwrap(e.WebMessageAsJson);
            if (payload is null)
            {
                return;
            }

            using var message = JsonDocument.Parse(payload);
            var root = message.RootElement;

            if (!root.TryGetProperty("type", out var type))
            {
                return;
            }

            var kind = type.GetString();
            if (kind == "imageFormat")
            {
                OnImageFormatRequested(root);
                return;
            }

            if (kind is not ("snippetQuery" or "imageQuery") ||
                !root.TryGetProperty("requestId", out var requestElement) ||
                !requestElement.TryGetInt64(out var requestId))
            {
                return;
            }

            var query = root.TryGetProperty("query", out var queryElement)
                ? queryElement.GetString() ?? string.Empty
                : string.Empty;

            var all = root.TryGetProperty("all", out var allElement) && allElement.ValueKind == JsonValueKind.True;

            object[] matches = kind switch
            {
                "snippetQuery" when _snippetSearch is not null => _snippetSearch(query)
                    .Select(choice => (object)new
                    {
                        name = choice.Name,
                        detail = choice.Detail,
                        html = choice.Html,
                        preview = string.Empty,
                    })
                    .ToArray(),
                "imageQuery" when _imageSearch is not null => _imageSearch(query, all)
                    .Select(choice => (object)new
                    {
                        name = choice.Name,
                        detail = choice.Detail,
                        html = choice.Html,
                        preview = choice.Preview,
                    })
                    .ToArray(),
                _ => [],
            };

            var script = "window.liftupSnippetResults(" + requestId + ", "
                         + JsonSerializer.Serialize(matches) + "); true";

            _ = Browser.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception)
        {
            // Eine fehlerhafte Suche darf die Eingabe nicht unterbrechen.
        }
    }

    /// <summary>
    /// Fragt die Anwendung nach den neuen Angaben zu einem angeklickten Bild und
    /// überträgt sie zurück in den Editor.
    /// </summary>
    private void OnImageFormatRequested(JsonElement root)
    {
        if (ImageFormatRequested is null)
        {
            return;
        }

        if (!root.TryGetProperty("id", out var idElement) || idElement.GetString() is not { Length: > 0 } id)
        {
            return;
        }

        string? Text(string name) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

        var current = ImageFormat.Parse(Text("size"), Text("place"), Text("flow"), Text("caption"));

        // Der Dialog läuft nach dem Beantworten der Nachricht, damit Chromium nicht blockiert.
        Dispatcher.BeginInvoke(() =>
        {
            if (ImageFormatRequested is null || Browser.CoreWebView2 is null)
            {
                return;
            }

            var format = ImageFormatRequested(new ImageFormatRequest(id, current));
            if (format is null)
            {
                return;
            }

            var script = "window.liftupApplyImageFormat(" + JsonSerializer.Serialize(new
            {
                id,
                size = format.SizeToken,
                place = format.PlacementToken,
                flow = format.FlowToken,
                caption = format.Caption ?? string.Empty,
            }) + "); true";

            _ = Browser.CoreWebView2.ExecuteScriptAsync(script);
        });
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
