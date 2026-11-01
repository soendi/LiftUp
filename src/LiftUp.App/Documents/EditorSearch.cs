using System.Net;
using LiftUp.App.Controls;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;

namespace LiftUp.App.Documents;

/// <summary>
/// Suche für die „/“- und „&amp;“-Eingabe in den Editoren. Textbausteine werden einmal
/// geladen; Bilder sucht der Editor bei jeder Eingabe im aktiven Projekt, mit „&amp;&amp;“
/// zusätzlich in der globalen Bibliothek. Das Projekt wird je Aufruf abgefragt, damit ein
/// Projektwechsel sofort wirkt.
/// </summary>
public sealed class EditorSearch
{
    private readonly AppServices _services;
    private readonly Func<Guid?> _projectId;
    private readonly Action<string> _reportError;
    private readonly HashSet<Guid> _scannedProjects = [];
    private IReadOnlyList<TextSnippet>? _snippetBodies;

    public EditorSearch(AppServices services, Func<Guid?> projectId, Action<string> reportError)
    {
        _services = services;
        _projectId = projectId;
        _reportError = reportError;
    }

    /// <summary>Vergisst die geladenen Textbausteine, damit ein neuer sofort gefunden wird.</summary>
    public void ForgetSnippets() => _snippetBodies = null;

    /// <summary>Textbausteine zur „/“-Eingabe, sortiert nach Trefferqualität.</summary>
    public IReadOnlyList<HtmlEditor.SnippetChoice> SearchSnippets(string query)
    {
        try
        {
            _snippetBodies ??= _services.Snippets.GetAll()
                .Select(item => _services.Snippets.Get(item.Id))
                .ToArray();

            return SnippetAutocomplete.Search(_snippetBodies, query)
                .Select(snippet => new HtmlEditor.SnippetChoice(
                    snippet.Name,
                    string.IsNullOrWhiteSpace(snippet.Category)
                        ? SnippetAutocomplete.Preview(snippet.Html)
                        : snippet.Category!,
                    snippet.Html))
                .ToArray();
        }
        catch (LiftUpException exception)
        {
            _reportError(exception.Message);
            return [];
        }
    }

    /// <summary>
    /// Gefüllte Felder der Tabellen zur „//“-Eingabe – nur aus dem gewählten Projekt. Jeder
    /// Vorschlag nennt Spalte und Herkunft („Jahr · Investitionsplanung“); bei mehreren
    /// Standorten trägt die Anlagebezeichnung ihren Standort mit. Eingefügt wird reiner Text.
    /// </summary>
    public IReadOnlyList<HtmlEditor.SnippetChoice> SearchTableValues(string query)
    {
        if (_projectId() is not { } projectId)
        {
            return [];
        }

        try
        {
            var tables = ProjectTableCatalog.Known
                .Select(definition => (definition, _services.ProjectTables.Get(projectId, definition.Key)))
                .ToArray();

            return ProjectTableValues.Search(tables, query, projectId: projectId)
                .Select(value => new HtmlEditor.SnippetChoice(
                    value.Text,
                    value.Detail,
                    WebUtility.HtmlEncode(value.Text)))
                .ToArray();
        }
        catch (LiftUpException exception)
        {
            _reportError(exception.Message);
            return [];
        }
    }

    /// <summary>
    /// Bilder zur „&amp;“-Eingabe: ohne <paramref name="includeGlobal"/> nur Dateien des
    /// Projekts, mit „&amp;&amp;“ zusätzlich die globale Bibliothek – nie andere Projekte.
    /// </summary>
    public IReadOnlyList<HtmlEditor.ImageChoice> SearchImages(string query, bool includeGlobal)
    {
        if (_projectId() is not { } projectId)
        {
            return [];
        }

        try
        {
            // Bilder, die im Projektordner liegen, werden einmal je Sitzung übernommen,
            // damit sie auch ohne den Umweg über die Bilderansicht auffindbar sind.
            if (_scannedProjects.Add(projectId))
            {
                _services.Images.SyncProjectFolder(projectId);
            }

            var choices = new List<HtmlEditor.ImageChoice>();
            foreach (var image in _services.Images.SearchForDocument(projectId, query, includeGlobal))
            {
                if (image.IsArchived)
                {
                    continue;
                }

                var html = ImageEmbedding.ToImageTag(image.FilePath, image.Name);
                if (html is null)
                {
                    continue;
                }

                var detail = string.Join(" · ", new[] { image.Category, image.Tags, image.ScopeText }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

                choices.Add(new HtmlEditor.ImageChoice(
                    image.Name,
                    detail,
                    ImageFormat.Wrap(html),
                    ImagePreviews.DataUri(image.FilePath) ?? string.Empty));
            }

            return choices;
        }
        catch (LiftUpException exception)
        {
            _reportError(exception.Message);
            return [];
        }
    }
}
