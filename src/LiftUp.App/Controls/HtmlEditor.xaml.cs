using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using Microsoft.Win32;
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
            /* Bilder: anklicken und mit der Maus verschieben */
            .liftup-fig { cursor: grab; -webkit-user-select: none; user-select: none; }
            .liftup-fig img { -webkit-user-drag: none; }
            /* Die Bildunterschrift wird direkt im Text bearbeitet. */
            .liftup-fig figcaption {
                cursor: text;
                -webkit-user-select: text;
                user-select: text;
            }
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

            // ---- Einfügen an genau der Cursorstelle ----
            window.liftupTopBlock = function (node) {
                var element = node && node.nodeType === 1 ? node : (node ? node.parentNode : null);
                while (element && element !== document.body) {
                    if (element.parentNode === document.body) {
                        return element;
                    }
                    element = element.parentNode;
                }
                return null;
            };

            // Nur diese Blöcke lassen sich gefahrlos teilen; Listen, Tabellen und
            // Bilder werden weiterhin vom Browser eingefügt.
            window.liftupCanSplit = function (block) {
                if (!block || block.nodeType !== 1) {
                    return false;
                }
                return /^(P|DIV|H1|H2|H3|H4|H5|H6|BLOCKQUOTE|PRE)$/.test(block.tagName);
            };

            window.liftupIsBlockNode = function (node) {
                if (!node || node.nodeType !== 1) {
                    return false;
                }
                return /^(P|DIV|H1|H2|H3|H4|H5|H6|UL|OL|LI|TABLE|FIGURE|BLOCKQUOTE|PRE|HR|SECTION)$/.test(node.tagName);
            };

            window.liftupHasBlockContent = function (fragment) {
                for (var index = 0; index < fragment.childNodes.length; index += 1) {
                    if (window.liftupIsBlockNode(fragment.childNodes[index])) {
                        return true;
                    }
                }
                return false;
            };

            window.liftupPlaceCaret = function (range) {
                var selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                window.liftupRange = range.cloneRange();
            };

            // Setzt fertige Blöcke ein und legt den Cursor dahinter ab.
            window.liftupInsertBlocks = function (range, html, parent, reference) {
                var fragment = range.createContextualFragment(html || '');
                var last = fragment.lastChild;
                parent.insertBefore(fragment, reference);
                if (last) {
                    var caret = document.createRange();
                    caret.setStartAfter(last);
                    caret.collapse(true);
                    window.liftupPlaceCaret(caret);
                }
                return true;
            };

            // Inhalte ohne Blöcke (Text, <br>, <b> …) kommen direkt an der Cursorstelle
            // hinein; der Browser teilt den Textknoten dort, Text davor und danach bleiben
            // stehen. execCommand('insertHTML') ersetzt in Chromium den ganzen Absatz und
            // löscht damit die Zeilen davor – deshalb wird der Inhalt selbst eingesetzt.
            window.liftupInsertInline = function (range, html) {
                var fragment = range.createContextualFragment(html || '');
                var last = fragment.lastChild;
                if (!last) {
                    return true;
                }

                range.insertNode(fragment);

                var caret = document.createRange();
                caret.setStartAfter(last);
                caret.collapse(true);
                window.liftupPlaceCaret(caret);
                return true;
            };

            // Blöcke, die sich nicht teilen lassen (Listen, Tabellen, Bilder): der Baustein
            // kommt unter den ganzen Block, statt dessen Inhalt zu ersetzen.
            window.liftupInsertAfterBlock = function (range, block, html) {
                var outer = block;
                while (outer.parentNode && outer.parentNode !== document.body
                    && /^(UL|OL|TABLE|TBODY|TR|DL)$/.test(outer.parentNode.tagName)) {
                    outer = outer.parentNode;
                }

                var parent = outer.parentNode || document.body;
                return window.liftupInsertBlocks(range, html, parent, outer.nextSibling);
            };

            // Fügt HTML an der aktuellen Stelle ein, ohne vorhandene Zeilen oder Absätze
            // zu entfernen. Blockweise Inhalte (Textbausteine, Bilder) werden als eigener
            // Block zwischen den Text davor und danach gesetzt; der Absatz an der
            // Einfügestelle bleibt bestehen, damit ein Zeilenumbruch davor erhalten bleibt.
            window.liftupInsertHtml = function (html) {
                var selection = window.getSelection();
                if (!selection || selection.rangeCount === 0) {
                    return false;
                }

                var range = selection.getRangeAt(0);
                if (!range.collapsed) {
                    range.deleteContents();
                }

                var probe = document.createRange().createContextualFragment(html || '');
                if (!window.liftupHasBlockContent(probe)) {
                    return window.liftupInsertInline(range, html);
                }

                var block = window.liftupTopBlock(range.startContainer);
                if (block && !window.liftupCanSplit(block)) {
                    return window.liftupInsertAfterBlock(range, block, html);
                }

                // Eine leere Zeile an der Einfügestelle bleibt stehen; der Baustein
                // kommt darunter, statt die Zeile zu ersetzen.
                if (block && !block.textContent.replace(/\u00a0/g, ' ').trim()) {
                    if (block.childNodes.length === 0) {
                        block.appendChild(document.createElement('br'));
                    }
                    return window.liftupInsertBlocks(range, html, block.parentNode, block.nextSibling);
                }

                // Den Inhalt hinter dem Cursor aus dem Absatz herausnehmen.
                var rest = document.createRange();
                rest.selectNodeContents(block || document.body);
                rest.setStart(range.startContainer, range.startOffset);
                var after = rest.extractContents();

                // Ein leer gewordener Absatz bleibt als Zeile stehen, damit nichts
                // nach oben rutscht.
                if (block && block.childNodes.length === 0) {
                    block.appendChild(document.createElement('br'));
                }

                var fragment = range.createContextualFragment(html || '');
                var parent = block ? block.parentNode : document.body;
                var reference = block ? block.nextSibling : null;
                var last = fragment.lastChild;
                parent.insertBefore(fragment, reference);

                // Ein leerer Rest (etwa ein leerer Textknoten am Zeilenende) erzeugt
                // keinen zusätzlichen Absatz unter dem Baustein.
                var hasTail = Array.prototype.some.call(after.childNodes, function (node) {
                    return node.nodeType === 1 || (node.nodeType === 3 && node.nodeValue.length > 0);
                });

                var tailBlock = null;
                if (hasTail) {
                    if (block) {
                        tailBlock = block.cloneNode(false);
                        tailBlock.appendChild(after);
                        parent.insertBefore(tailBlock, reference);
                    } else {
                        parent.insertBefore(after, reference);
                    }
                }

                var caret = document.createRange();
                if (tailBlock) {
                    caret.setStart(tailBlock, 0);
                } else if (last) {
                    caret.setStartAfter(last);
                } else {
                    return true;
                }
                caret.collapse(true);
                window.liftupPlaceCaret(caret);
                return true;
            };

            window.liftupInsertHtmlAtSelection = function (html) {
                document.body.focus();
                var selection = window.getSelection();
                if (window.liftupRange) {
                    selection.removeAllRanges();
                    selection.addRange(window.liftupRange);
                }
                return window.liftupInsertHtml(html);
            };

            // ---- Vorschläge für Textbausteine („/Text“) ----
            window.liftupSnippetEnabled = false;
            window.liftupSnippetOpen = false;
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

            // Die Trefferliste zeigt die Anwendung in einem eigenen Fenster über dem
            // Editor an; der Editor meldet dafür nur Anzeigen und Schließen.
            window.liftupHideSnippets = function () {
                if (!window.liftupSnippetOpen) {
                    return;
                }
                window.liftupSnippetOpen = false;
                try {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'snippetHide' }));
                } catch (error) {
                    // Ohne Verbindung zur Anwendung bleibt nur das Schließen.
                }
            };

            window.liftupCloseSnippets = function () {
                window.liftupHideSnippets();
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

                // „//feld“ sucht in den gefüllten Feldern der Zustandsanalyse (etwa der
                // Anlagebezeichnung). Zwei Schrägstriche, damit „/“ die Textbausteine behält.
                var table = /(^|[\s\u00a0>])\/\/([^\s\/]{0,60})$/.exec(before);
                if (table) {
                    return {
                        kind: 'table',
                        all: false,
                        node: node,
                        start: offset - table[2].length - 2,
                        length: table[2].length + 2,
                        query: table[2]
                    };
                }

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

            // Die Anwendung zeigt die Treffer in einem eigenen Fenster über dem Editor;
            // der Editor übermittelt dafür Treffer, Auswahl und Cursorstelle.
            window.liftupRenderSnippets = function () {
                var rect = window.liftupCaretRect();
                if (!rect) {
                    return;
                }

                var items = window.liftupSnippetItems.map(function (item) {
                    return {
                        name: item.name || '',
                        detail: item.detail || '',
                        preview: item.preview || ''
                    };
                });

                window.liftupSnippetOpen = true;
                try {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: 'snippetShow',
                        kind: window.liftupSnippetKind,
                        index: window.liftupSnippetIndex,
                        left: Math.max(0, rect.left),
                        top: rect.bottom + 2,
                        items: items
                    }));
                } catch (error) {
                    window.liftupSnippetOpen = false;
                }
            };

            // Übernimmt den Treffer, den die Anwendung im Vorschlagenfenster angeklickt hat.
            window.liftupPickSnippet = function (position) {
                var item = window.liftupSnippetItems[position];
                if (item) {
                    window.liftupInsertSnippet(item);
                }
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
                        type: token.kind === 'image' ? 'imageQuery'
                            : (token.kind === 'table' ? 'tableQuery' : 'snippetQuery'),
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
                window.liftupInsertHtml(item.html || '');
            };

            document.addEventListener('keydown', function (event) {
                if (!window.liftupSnippetEnabled || !window.liftupSnippetOpen) {
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

            // Meldet der Anwendung, ob im Text etwas steht – die Analyse färbt damit ihre Reiter.
            window.liftupHasContent = function () {
                var body = document.body;
                if (!body) {
                    return false;
                }
                if (body.querySelector('img, table, ul, ol')) {
                    return true;
                }
                return !!body.textContent.replace(/\u00a0/g, ' ').trim();
            };

            document.addEventListener('input', function () {
                window.liftupSnippetQuery();
                try {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: 'changed',
                        filled: window.liftupHasContent()
                    }));
                } catch (error) {
                    // Ohne Verbindung zur Anwendung bleibt die Farbe beim letzten Stand.
                }
            });

            // Ein Klick in den Text beendet die Vorschläge, die nicht mehr zur Stelle passen.
            document.addEventListener('mouseup', function () {
                if (window.liftupSnippetOpen) {
                    window.liftupSnippetQuery();
                }
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
            window.liftupFormatTarget = null;

            window.liftupFigId = function (figure) {
                if (!figure.dataset.liftupId) {
                    // Kennungen aus gespeichertem HTML bleiben gültig: jede neue
                    // Kennung wird geprüft, damit keine doppelt vergeben wird.
                    var id;
                    do {
                        window.liftupFigCounter += 1;
                        id = 'fig' + window.liftupFigCounter;
                    } while (document.querySelector('[data-liftup-id="' + id + '"]'));
                    figure.dataset.liftupId = id;
                }
                return figure.dataset.liftupId;
            };

            window.liftupOpenFormat = function (figure) {
                // Das angeklickte Bild bleibt festgehalten, damit die Angaben
                // sicher an dieses Bild zurückgehen.
                window.liftupFormatTarget = figure;
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
                figure.className = 'liftup-fig liftup-fig-' + size + ' liftup-fig-' + place + ' liftup-fig-' + flow
                    + (figure.querySelector('figcaption') ? ' liftup-fig-captioned' : '');
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

                // Die Unterschrift wird direkt im Text geschrieben, nicht gezogen.
                if (event.target.closest('figcaption')) {
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

                // Zuerst das zuletzt angeklickte Bild, damit die Angaben nicht bei
                // einem gleichnamigen Bild an anderer Stelle landen.
                var figure = window.liftupFormatTarget;
                window.liftupFormatTarget = null;
                if (!figure || !figure.isConnected || figure.dataset.liftupId !== value.id) {
                    figure = document.querySelector('[data-liftup-id="' + value.id + '"]');
                }
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
                    figure.classList.add('liftup-fig-captioned');
                } else if (caption && caption.parentNode) {
                    caption.parentNode.removeChild(caption);
                    figure.classList.remove('liftup-fig-captioned');
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

            // Bild und Bildunterschrift gehören zusammen: eine Unterschrift, die
            // außerhalb des Bildes steht, wird wieder hineingeholt.
            window.liftupNormalizeFigures = function () {
                var captions = document.querySelectorAll('figcaption');
                for (var index = 0; index < captions.length; index += 1) {
                    var caption = captions[index];
                    var parent = caption.parentNode;
                    if (parent && parent.classList && parent.classList.contains('liftup-fig')) {
                        parent.classList.add('liftup-fig-captioned');
                        continue;
                    }

                    var previous = caption.previousElementSibling;
                    if (previous && previous.tagName === 'FIGURE' && previous.classList.contains('liftup-fig')) {
                        previous.appendChild(caption);
                        previous.classList.add('liftup-fig-captioned');
                    }
                }

                var figures = document.querySelectorAll('figure.liftup-fig');
                for (var position = 0; position < figures.length; position += 1) {
                    if (figures[position].querySelector('figcaption')) {
                        figures[position].classList.add('liftup-fig-captioned');
                    } else {
                        figures[position].classList.remove('liftup-fig-captioned');
                    }
                }
            };

            // Eine leer gelöschte Unterschrift verschwindet samt Block-Regel.
            document.addEventListener('focusout', function (event) {
                var caption = event.target;
                if (!caption || !caption.matches || !caption.matches('figcaption')) {
                    return;
                }
                if (caption.textContent.trim()) {
                    return;
                }

                var figure = caption.parentNode;
                if (figure && figure.classList) {
                    figure.classList.remove('liftup-fig-captioned');
                }
                if (caption.parentNode) {
                    caption.parentNode.removeChild(caption);
                }
            }, true);

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
    private Func<string, IReadOnlyList<SnippetChoice>>? _tableSearch;
    private AppServices? _snippetServices;
    private AppServices? _imageServices;
    private Func<Guid?>? _imageProjectId;

    /// <summary>Meldet jede Änderung im Text – die Analyse färbt damit ihre Reiter.</summary>
    public event EventHandler? ContentChanged;

    /// <summary><c>true</c>, solange Text, eine Liste, eine Tabelle oder ein Bild im Feld steht.</summary>
    public bool HasText { get; private set; }

    public HtmlEditor()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        // Ohne WebView2 zählt der Ersatztext; die Reiterfarbe soll auch dort mitgehen.
        FallbackBox.TextChanged += (_, _) =>
        {
            if (!_failed)
            {
                return;
            }

            HasText = !string.IsNullOrWhiteSpace(FallbackBox.Text);
            ContentChanged?.Invoke(this, EventArgs.Empty);
        };

        // Das Vorschlagenfenster ist ein eigenes Fenster über dem Editor und
        // verschwindet mit ihm.
        Unloaded += (_, _) => SnippetPopup.IsOpen = false;

        // Ein Klick auf ein Bild im Text öffnet die Bild-Eigenschaften: Größe,
        // Ausrichtung, Textfluss und Unterschrift. Jeder Editor bringt das Fenster mit;
        // eine Ansicht kann es über die Eigenschaft durch etwas anderes ersetzen.
        ImageFormatRequested = request =>
        {
            var dialog = new Views.ImageFormatWindow(request.Current);
            if (Window.GetWindow(this) is { } owner)
            {
                dialog.Owner = owner;
            }

            return dialog.ShowDialog() == true ? dialog.Format : null;
        };
    }

    /// <summary>Zeigt den übergebenen HTML-Inhalt im Editor an.</summary>
    public void SetHtml(string? html)
    {
        _pendingHtml = html ?? string.Empty;
        HasText = HasVisibleContent(_pendingHtml);

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

    /// <summary>
    /// Schaltet die Feldersuche ein: nach „//“ schlägt der Editor die gefüllten Felder der
    /// Zustandsanalyse vor, etwa die Anlagebezeichnung samt Standort.
    /// </summary>
    public void EnableTableAutocomplete(Func<string, IReadOnlyList<SnippetChoice>> search)
    {
        _tableSearch = search;

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

    /// <summary>
    /// Zeigt die Treffer der „/“- und „&amp;“-Suche in einem eigenen Fenster direkt unter
    /// dem Cursor. Es liegt über dem Editor, damit die Liste nicht am Textfeld abgeschnitten
    /// oder von der Oberfläche daneben überdeckt wird.
    /// </summary>
    private void ShowSnippets(JsonElement root)
    {
        var items = root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToArray()
            : [];

        var index = root.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var active)
            ? active
            : 0;

        var kindName = root.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
        var isImage = kindName == "image";

        SnippetList.Children.Clear();

        if (items.Length == 0)
        {
            SnippetList.Children.Add(new TextBlock
            {
                Text = isImage
                    ? "Keine Bilder gefunden"
                    : kindName == "table" ? "Keine Felder gefunden" : "Keine Textbausteine gefunden",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x86)),
                Margin = new Thickness(8, 6, 8, 6),
            });
        }
        else
        {
            for (var position = 0; position < items.Length; position++)
            {
                SnippetList.Children.Add(CreateSnippetRow(items[position], position, position == index));
            }
        }

        // Der Editor liefert die Cursorstelle in seinen Pixeln; das Fenster rechnet sie
        // in die Koordinaten dieses Steuerelements um.
        var left = root.TryGetProperty("left", out var leftElement) ? leftElement.GetDouble() : 0;
        var top = root.TryGetProperty("top", out var topElement) ? topElement.GetDouble() : 0;
        var origin = Browser.TranslatePoint(new Point(left, top), this);

        SnippetPopup.PlacementTarget = this;
        SnippetPopup.Placement = PlacementMode.Relative;
        SnippetPopup.HorizontalOffset = Math.Max(0, origin.X);
        SnippetPopup.VerticalOffset = Math.Max(0, origin.Y);
        SnippetPopup.IsOpen = true;
    }

    private UIElement CreateSnippetRow(JsonElement item, int position, bool active)
    {
        string Text(string name) =>
            item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : string.Empty;

        var texts = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
        };

        texts.Children.Add(new TextBlock
        {
            Text = Text("name"),
            FontSize = 13,
            MaxWidth = 300,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x44)),
        });

        var detail = Text("detail");
        if (detail.Length > 0)
        {
            texts.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                MaxWidth = 300,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x86)),
            });
        }

        var content = new StackPanel { Orientation = Orientation.Horizontal };

        if (PreviewImage(Text("preview")) is { } preview)
        {
            content.Children.Add(new Image
            {
                Source = preview,
                Width = 44,
                Height = 32,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 10, 0),
            });
        }

        content.Children.Add(texts);

        var row = new Border
        {
            Background = active
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE))
                : Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5, 8, 5),
            Cursor = Cursors.Hand,
            Child = content,
        };

        row.MouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            PickSnippet(position);
        };

        return row;
    }

    /// <summary>Übernimmt den im Vorschlagenfenster angeklickten Treffer in den Text.</summary>
    private void PickSnippet(int position)
    {
        SnippetPopup.IsOpen = false;

        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        // Der Klick in das eigene Fenster nimmt dem Editor die Eingabe; er bekommt sie zurück.
        Browser.Focus();
        _ = Browser.CoreWebView2.ExecuteScriptAsync("window.liftupPickSnippet(" + position + "); true");
    }

    /// <summary>Wandelt das Vorschaubild eines Treffers (Daten-URI) in ein Bild um.</summary>
    private static ImageSource? PreviewImage(string preview)
    {
        var comma = preview.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(preview[(comma + 1)..]));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or ArgumentException)
        {
            // Ein fehlerhaftes Vorschaubild darf die Liste nicht verhindern.
            return null;
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

            if (kind == "snippetHide")
            {
                SnippetPopup.IsOpen = false;
                return;
            }

            if (kind == "changed")
            {
                HasText = root.TryGetProperty("filled", out var filled) && filled.ValueKind == JsonValueKind.True;
                ContentChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (kind == "snippetShow")
            {
                ShowSnippets(root);
                return;
            }

            if (kind is not ("snippetQuery" or "imageQuery" or "tableQuery") ||
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
                "tableQuery" when _tableSearch is not null => _tableSearch(query)
                    .Select(choice => (object)new
                    {
                        name = choice.Name,
                        detail = choice.Detail,
                        html = choice.Html,
                        preview = string.Empty,
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
                     + "; window.liftupNormalizeFigures && window.liftupNormalizeFigures(); true";

        await Browser.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void ShowFallback()
    {
        _failed = true;
        _ready = false;

        SnippetPopup.IsOpen = false;
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

    /// <summary>
    /// Dienste, mit denen sich markierter Text als Textvorlage ablegen lässt. Ohne Dienste
    /// bleibt der Knopf „Als Textvorlage speichern“ aus.
    /// </summary>
    public AppServices? SnippetServices
    {
        get => _snippetServices;
        set
        {
            _snippetServices = value;
            SaveSnippetButton.IsEnabled = value is not null;
        }
    }

    /// <summary>
    /// Legt den markierten Text als neuen Textbaustein ab: Der Dialog fragt Name und Kategorie
    /// ab, gespeichert wird über die Textbausteine. Danach steht der Text über „/“ bereit.
    /// </summary>
    private async void OnSaveSnippetClick(object sender, RoutedEventArgs e)
    {
        if (_snippetServices is not { } services)
        {
            return;
        }

        var owner = Window.GetWindow(this);

        string? html;
        try
        {
            html = _failed ? FallbackSelectionHtml() : await GetSelectionHtmlAsync();
        }
        catch (Exception exception)
        {
            Show(owner, "Der markierte Text konnte nicht gelesen werden.\n\n" + exception.Message,
                MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            Show(owner, "Bitte zuerst den Text markieren, der als Textvorlage gespeichert werden soll.",
                MessageBoxImage.Information);
            return;
        }

        var dialog = new Views.SnippetSaveWindow(html);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var snippet = services.Snippets.Create(new SnippetInput
            {
                Name = dialog.SnippetName,
                Category = dialog.Category,
                Html = html,
            });

            Show(owner, $"Textvorlage gespeichert: {snippet.Name}. Sie steht ab jetzt über „/“ bereit.",
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is LiftUpException or InvalidOperationException)
        {
            Show(owner, exception.Message, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Schaltet „Bild einfügen …“ ein. Gewählte Bilder werden bei Bedarf in den
    /// Projektordner kopiert und als Figur mit Größe, Ausrichtung und Textfluss
    /// in den Text gesetzt. Ohne Projektquelle bleibt die Datei, wo sie ist.
    /// </summary>
    public void EnableImageInsert(AppServices services, Func<Guid?>? projectId = null)
    {
        _imageServices = services;
        _imageProjectId = projectId;
        InsertImageButton.IsEnabled = true;
    }

    /// <summary>
    /// „Bild einfügen …“: Datei auswählen, in den Projektordner übernehmen und an der
    /// Cursorstelle einsetzen. Die Eigenschaften lassen sich danach per Klick auf das
    /// Bild ändern.
    /// </summary>
    private async void OnInsertImageClick(object sender, RoutedEventArgs e)
    {
        if (_imageServices is not { } services)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        var dialog = new OpenFileDialog
        {
            Title = "Bild auswählen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.svg|Alle Dateien|*.*",
        };

        if (dialog.ShowDialog(owner) != true || string.IsNullOrEmpty(dialog.FileName))
        {
            return;
        }

        string path;
        try
        {
            path = PlaceInProjectFolder(services, dialog.FileName);
        }
        catch (Exception exception) when (
            exception is LiftUpException or IOException or UnauthorizedAccessException)
        {
            Show(owner, "Das Bild konnte nicht in den Projektordner übernommen werden.\n\n" + exception.Message,
                MessageBoxImage.Warning, "Bild einfügen");
            return;
        }

        var html = ImageEmbedding.ToImageTag(path, Path.GetFileNameWithoutExtension(path));
        if (html is null)
        {
            Show(owner, "Diese Bilddatei kann nicht dargestellt werden. Möglich sind PNG, JPG, GIF, WebP, BMP und SVG.",
                MessageBoxImage.Warning, "Bild einfügen");
            return;
        }

        try
        {
            await InsertHtmlAsync(ImageFormat.Wrap(html));
        }
        catch (InvalidOperationException exception)
        {
            Show(owner, exception.Message, MessageBoxImage.Warning, "Bild einfügen");
        }
    }

    /// <summary>
    /// Liefert den Pfad des Bildes im Projektordner: Liegt die Datei schon dort, bleibt sie
    /// unverändert. Sonst wird sie in den Unterordner <c>02_Bilder</c> kopiert, wobei eine
    /// vorhandene Datei gleichen Namens nicht überschrieben wird. Ohne Projekt entfällt die Kopie.
    /// </summary>
    private string PlaceInProjectFolder(AppServices services, string sourcePath)
    {
        if (_imageProjectId?.Invoke() is not { } projectId)
        {
            return sourcePath;
        }

        var project = services.Projects.GetProject(projectId);
        if (string.IsNullOrWhiteSpace(project.FolderPath))
        {
            return sourcePath;
        }

        var root = Path.GetFullPath(project.FolderPath);
        var source = Path.GetFullPath(sourcePath);
        if (source.StartsWith(
                root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var folder = Path.Combine(root, ProjectFolders.Images);
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, Path.GetFileName(source));
        var counter = 2;
        while (File.Exists(target) && !HasSameContent(target, source))
        {
            target = Path.Combine(folder,
                $"{Path.GetFileNameWithoutExtension(source)} ({counter}){Path.GetExtension(source)}");
            counter++;
        }

        if (!File.Exists(target))
        {
            File.Copy(source, target, overwrite: false);
        }

        // Damit das Bild sofort in der Bildbibliothek steht, wird der Projektordner
        // eingelesen; scheitert das, bleibt das Bild trotzdem im Text.
        try
        {
            services.Images.SyncProjectFolder(projectId);
        }
        catch (Exception exception) when (exception is LiftUpException or IOException or UnauthorizedAccessException)
        {
            // Der Abgleich ist nur eine Bequemlichkeit.
        }

        return target;
    }

    /// <summary>Vergleicht zwei Dateien über ihren Inhalt.</summary>
    private static bool HasSameContent(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        return File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
    }

    /// <summary>Der markierte Teil des Ersatzfelds als HTML; Zeilenumbrüche bleiben erhalten.</summary>
    private string? FallbackSelectionHtml()
    {
        var selected = FallbackBox.SelectedText;
        if (string.IsNullOrWhiteSpace(selected))
        {
            return null;
        }

        return WebUtility.HtmlEncode(selected.Trim())
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "<br />", StringComparison.Ordinal);
    }

    /// <summary>Meldung mit dem Fenster als Besitzer, sobald es eines gibt.</summary>
    private static void Show(Window? owner, string message, MessageBoxImage icon,
        string title = "Als Textvorlage speichern")
    {
        if (owner is null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, icon);
            return;
        }

        MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
    }

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
                "window.liftupInsertHtmlAtSelection(" + JsonSerializer.Serialize(html) + "); true");
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

    /// <summary>
    /// Ob im HTML etwas Sichtbares steht: Text, eine Liste, eine Tabelle oder ein Bild.
    /// Leere Absätze und Platzhalter zählen nicht.
    /// </summary>
    private static bool HasVisibleContent(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        if (html.Contains("<img", StringComparison.OrdinalIgnoreCase)
            || html.Contains("<table", StringComparison.OrdinalIgnoreCase)
            || html.Contains("<ul", StringComparison.OrdinalIgnoreCase)
            || html.Contains("<ol", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = Regex.Replace(html, "<[^>]*>", " ")
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(text));
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
