using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LiftUp.App.Controls;
using LiftUp.App.Documents;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>
/// Füllt die Abschnitte einer Vorlage aus: pro Untertitel ein Tab mit Text und
/// Bildern. Die Tabs sind nur für dieses Dokument gedacht und lassen sich
/// umbenennen, verschieben, löschen und ergänzen.
/// </summary>
public partial class DocumentComposerWindow : Window
{
    private sealed record ProjectOption(Guid Id, string DisplayText);

    private sealed record FormatOption(string Value, string Name);

    private sealed record OrientationOption(bool Landscape, string Name);

    private sealed class SectionDraft
    {
        public required TabItem Tab { get; init; }
        public required TextBox TitleBox { get; init; }
        public required HtmlEditor Editor { get; init; }

        public string Title => TitleBox.Text.Trim();
    }

    private static readonly FormatOption[] Formats = [new("A4", "A4"), new("A3", "A3")];
    private static readonly OrientationOption[] Orientations = [new(false, "Hochformat"), new(true, "Querformat")];

    private readonly AppServices _services;
    private readonly List<SectionDraft> _sections = [];
    private readonly Guid? _presetProjectId;
    private readonly Guid? _presetTemplateId;
    private IReadOnlyList<DocumentSectionContent>? _presetSections;
    private IReadOnlyList<TextSnippet>? _snippetBodies;
    private readonly HashSet<Guid> _scannedProjects = [];
    private string? _lastPdfPath;

    public DocumentComposerWindow(
        AppServices services,
        Guid? projectId = null,
        Guid? templateId = null,
        IReadOnlyList<DocumentSectionContent>? sections = null)
    {
        _services = services;
        _presetProjectId = projectId;
        _presetTemplateId = templateId;
        _presetSections = sections;

        InitializeComponent();

        FormatBox.ItemsSource = Formats;
        FormatBox.SelectedIndex = 0;
        OrientationBox.ItemsSource = Orientations;
        OrientationBox.SelectedIndex = 0;

        LoadProjects();
    }

    private ProjectOption? SelectedProject => ProjectBox.SelectedItem as ProjectOption;

    private SectionDraft? CurrentDraft =>
        _sections.FirstOrDefault(draft => ReferenceEquals(draft.Tab, SectionTabs.SelectedItem));

    private void LoadProjects()
    {
        try
        {
            var projects = _services.Projects.Search(new ProjectQuery())
                .Select(item => new ProjectOption(item.Id, $"{item.Number} · {item.Name}"))
                .ToArray();

            ProjectBox.ItemsSource = projects;

            var index = _presetProjectId is { } preset
                ? Array.FindIndex(projects, item => item.Id == preset)
                : 0;
            ProjectBox.SelectedIndex = projects.Length == 0 ? -1 : Math.Max(index, 0);

            if (projects.Length == 0)
            {
                InfoText.Text = "Noch keine Projekte vorhanden.";
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        // Die Vorlagen hängen nicht vom Projekt ab; nur die Auswahl bleibt erhalten.
        if (TemplateBox.ItemsSource is null)
        {
            LoadTemplates();
        }
    }

    private void OnTemplateChanged(object sender, SelectionChangedEventArgs e) => BuildTabs();

    private void LoadTemplates()
    {
        try
        {
            var templates = _services.Templates.GetAll(includeInactive: false);
            TemplateBox.ItemsSource = templates;

            var index = _presetTemplateId is { } preset
                ? templates.ToList().FindIndex(item => item.Id == preset)
                : 0;
            TemplateBox.SelectedIndex = templates.Count == 0 ? -1 : Math.Max(index, 0);

            if (templates.Count == 0)
            {
                InfoText.Text = "Keine aktive Vorlage vorhanden. Bitte unter „Vorlagen“ eine anlegen.";
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    /// <summary>Legt je definiertem Untertitel einen Tab an.</summary>
    private void BuildTabs()
    {
        foreach (var draft in _sections)
        {
            draft.Tab.Content = null;
        }

        _sections.Clear();
        SectionTabs.Items.Clear();

        if (TemplateBox.SelectedItem is not TemplateListItem template)
        {
            return;
        }

        IReadOnlyList<TemplateSection> defined;
        try
        {
            defined = _services.Templates.GetSections(template.Id);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
            return;
        }

        if (_presetSections is { Count: > 0 } preset)
        {
            _presetSections = null;
            foreach (var section in preset)
            {
                AddSection(section.Title, section.Html);
            }

            InfoText.Text = $"Gespeicherte Abschnitte aus „{template.Name}“ geladen. Änderungen ergeben ein neues PDF.";
            return;
        }

        if (defined.Count == 0)
        {
            AddSection("Inhalt");
            InfoText.Text = "Diese Vorlage hat noch keine Abschnitte. Unter „Vorlagen“ lassen sich Untertitel festlegen; hier geht auch ein eigener Tab.";
            return;
        }

        foreach (var section in defined)
        {
            AddSection(section.Title);
        }

        InfoText.Text = $"{template.Name}: {defined.Count} Abschnitt(e) fließen mit Abstand untereinander; der erste beginnt auf Seite 2. Im Text mit /Textbaustein und &Bild suchen (&&Bild für alle Projekte).";
    }

    private void AddSection(string title, string? html = null)
    {
        var heading = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(title) ? $"Abschnitt {_sections.Count + 1}" : title.Trim(),
        };

        var titleBox = new TextBox { Text = heading.Text, Width = 320 };
        titleBox.TextChanged += (_, _) =>
            heading.Text = string.IsNullOrWhiteSpace(titleBox.Text) ? "Ohne Titel" : titleBox.Text.Trim();

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        titleRow.Children.Add(new TextBlock
        {
            Text = "Untertitel",
            Style = (Style)FindResource("LiftUpFieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        titleRow.Children.Add(titleBox);

        var insertSnippet = new Button { Content = "Textbaustein einfügen" };
        insertSnippet.Click += OnInsertSnippetClick;

        var saveAsSnippet = new Button { Content = "Auswahl als Textbaustein …" };
        saveAsSnippet.Click += OnSaveSelectionAsSnippetClick;

        var insertImage = new Button { Content = "Bild einfügen", Margin = new Thickness(0) };
        insertImage.Click += OnInsertImageClick;

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        buttonRow.Children.Add(insertSnippet);
        buttonRow.Children.Add(saveAsSnippet);
        buttonRow.Children.Add(insertImage);

        var editor = new HtmlEditor();
        editor.EnableSnippetAutocomplete(SearchSnippets);
        editor.EnableImageAutocomplete(SearchImages);
        if (!string.IsNullOrWhiteSpace(html))
        {
            editor.SetHtml(html);
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(titleRow, 0);
        Grid.SetRow(buttonRow, 1);
        Grid.SetRow(editor, 2);
        grid.Children.Add(titleRow);
        grid.Children.Add(buttonRow);
        grid.Children.Add(editor);

        var tab = new TabItem { Header = heading, Content = grid };
        _sections.Add(new SectionDraft { Tab = tab, TitleBox = titleBox, Editor = editor });
        SectionTabs.Items.Add(tab);
        SectionTabs.SelectedItem = tab;
    }

    /// <summary>
    /// Sucht für die „/“-Eingabe im Editor nach Textbausteinen. Die Inhalte werden
    /// beim ersten Aufruf geladen und bis zum Schließen des Fensters wiederverwendet.
    /// </summary>
    private IReadOnlyList<HtmlEditor.SnippetChoice> SearchSnippets(string query)
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
            InfoText.Text = exception.Message;
            return [];
        }
    }

    /// <summary>
    /// Sucht für die „&amp;“-Eingabe im Editor nach Bildern. „&amp;“ durchsucht die Bilder
    /// des aktiven Projekts (globale Bibliothek plus Projektbilder), „&amp;&amp;“ alle.
    /// Jeder Treffer bringt ein kleines Vorschaubild für die Liste mit.
    /// </summary>
    private IReadOnlyList<HtmlEditor.ImageChoice> SearchImages(string query, bool allProjects)
    {
        var project = SelectedProject;
        if (project is null)
        {
            return [];
        }

        try
        {
            // Bilder, die im Projektordner liegen, werden einmal je Fenster übernommen,
            // damit sie auch ohne den Umweg über die Bilderansicht auffindbar sind.
            if (_scannedProjects.Add(project.Id))
            {
                _services.Images.SyncProjectFolder(project.Id);
            }

            var images = _services.Images.Search(project.Id, query, allProjects: allProjects);
            var choices = new List<HtmlEditor.ImageChoice>();

            foreach (var image in images)
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
                    html,
                    ImagePreviews.DataUri(image.FilePath) ?? string.Empty));
            }

            return choices;
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
            return [];
        }
    }

    private void OnAddSectionClick(object sender, RoutedEventArgs e) => AddSection(string.Empty);

    private void OnRemoveSectionClick(object sender, RoutedEventArgs e)
    {
        var draft = CurrentDraft;
        if (draft is null)
        {
            return;
        }

        _sections.Remove(draft);
        SectionTabs.Items.Remove(draft.Tab);
        draft.Tab.Content = null;
    }

    private void OnMoveLeftClick(object sender, RoutedEventArgs e) => MoveSection(-1);

    private void OnMoveRightClick(object sender, RoutedEventArgs e) => MoveSection(1);

    private void MoveSection(int offset)
    {
        var draft = CurrentDraft;
        if (draft is null)
        {
            return;
        }

        var index = _sections.IndexOf(draft);
        var target = index + offset;
        if (target < 0 || target >= _sections.Count)
        {
            return;
        }

        _sections.RemoveAt(index);
        _sections.Insert(target, draft);
        SectionTabs.Items.Remove(draft.Tab);
        SectionTabs.Items.Insert(target, draft.Tab);
        SectionTabs.SelectedItem = draft.Tab;
    }

    private async void OnInsertSnippetClick(object sender, RoutedEventArgs e)
    {
        var draft = CurrentDraft;
        if (draft is null)
        {
            return;
        }

        try
        {
            var snippets = _services.Snippets.GetAll();
            if (snippets.Count == 0)
            {
                InfoText.Text = "Es sind noch keine Textbausteine vorhanden.";
                return;
            }

            var picker = new PickerWindow(
                "Textbaustein einfügen",
                snippets.Select(item => new PickerWindow.Item(item.Id, item.Name, item.Category))) { Owner = this };

            if (picker.ShowDialog() != true || picker.Selected is null)
            {
                return;
            }

            var snippet = _services.Snippets.Get(picker.Selected.Id);
            await draft.Editor.InsertHtmlAsync(snippet.Html);
        }
        catch (Exception exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    /// <summary>Speichert den im Abschnitt markierten Text als neuen Textbaustein.</summary>
    private async void OnSaveSelectionAsSnippetClick(object sender, RoutedEventArgs e)
    {
        var draft = CurrentDraft;
        if (draft is null)
        {
            return;
        }

        try
        {
            var html = await draft.Editor.GetSelectionHtmlAsync();
            if (string.IsNullOrWhiteSpace(html))
            {
                InfoText.Text = "Bitte zuerst den Text im Abschnitt markieren, der als Textbaustein gespeichert werden soll.";
                return;
            }

            var dialog = new SnippetSaveWindow(html) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var snippet = _services.Snippets.Create(new SnippetInput
            {
                Name = dialog.SnippetName,
                Category = dialog.Category,
                Html = html,
            });

            // Damit die „/“-Suche den neuen Baustein sofort kennt.
            _snippetBodies = null;
            InfoText.Text = $"„{snippet.Name}“ als Textbaustein gespeichert – im Text mit /{snippet.Name} einfügbar.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Der Textbaustein konnte nicht gespeichert werden: " + exception.Message;
        }
    }

    private async void OnInsertImageClick(object sender, RoutedEventArgs e)
    {
        var draft = CurrentDraft;
        var project = SelectedProject;
        if (draft is null || project is null)
        {
            return;
        }

        try
        {
            var images = _services.Images.Search(project.Id, null);
            if (images.Count == 0)
            {
                InfoText.Text = "Es sind noch keine Bilder vorhanden. Bilder lassen sich im Reiter „Bilder“ erfassen.";
                return;
            }

            var picker = new PickerWindow(
                "Bild einfügen",
                images.Select(item => new PickerWindow.Item(item.Id, item.Name, $"{item.Category} · {item.ScopeText}"))) { Owner = this };

            if (picker.ShowDialog() != true || picker.Selected is null)
            {
                return;
            }

            var tag = _services.Images.GetImageTag(project.Id, picker.Selected.Name);
            if (tag is null)
            {
                InfoText.Text = "Die Bilddatei ist nicht mehr vorhanden.";
                return;
            }

            await draft.Editor.InsertHtmlAsync($"<p>{tag}</p>");
        }
        catch (Exception exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private async void OnCreatePdfClick(object sender, RoutedEventArgs e) => await CreatePdfAsync();

    private async Task CreatePdfAsync()
    {
        var project = SelectedProject;
        if (project is null || TemplateBox.SelectedItem is not TemplateListItem template)
        {
            InfoText.Text = "Bitte Projekt und Vorlage wählen.";
            return;
        }

        // Fehlende Bilder vor dem Erzeugen melden – geschrieben wird dann nichts.
        IReadOnlyList<string> missingImages;
        try
        {
            missingImages = _services.Documents.FindMissingImages(project.Id, template.Id);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
            return;
        }

        if (missingImages.Count > 0)
        {
            InfoText.Text = "PDF nicht erzeugt: In der Vorlage fehlen Bilder.";

            var fix = new MissingImagesWindow(_services, project.Id, template.Id, missingImages)
            {
                Owner = this,
            };

            if (fix.ShowDialog() == true)
            {
                InfoText.Text = fix.PlaceholdersRemoved
                    ? "Platzhalter entfernt – PDF wird erneut erzeugt …"
                    : "Bilder ersetzt – PDF wird erneut erzeugt …";

                await CreatePdfAsync();
            }

            return;
        }

        var contents = new List<DocumentSectionContent>();
        try
        {
            foreach (var draft in _sections)
            {
                contents.Add(new DocumentSectionContent(draft.Title, await draft.Editor.GetHtmlAsync()));
            }
        }
        catch (Exception exception)
        {
            InfoText.Text = exception.Message;
            return;
        }

        if (contents.Count == 0 || contents.All(item => string.IsNullOrWhiteSpace(item.Html)))
        {
            InfoText.Text = "Es ist noch kein Inhalt erfasst.";
            return;
        }

        var format = (FormatBox.SelectedItem as FormatOption)?.Value ?? "A4";
        var landscape = (OrientationBox.SelectedItem as OrientationOption)?.Landscape ?? false;
        var margin = double.TryParse(MarginBox.Text, out var parsed) ? parsed : 15;

        try
        {
            InfoText.Text = "PDF wird erzeugt …";
            var document = await _services.Documents.CreatePdfFromSectionsAsync(
                project.Id,
                template.Id,
                contents,
                new PdfPageSettings(format, landscape, margin));

            InfoText.Text = $"Gespeichert: {document.FilePath}";
            _lastPdfPath = document.FilePath;
            SendMailButton.IsEnabled = true;

            var answer = MessageBox.Show(
                $"Das Dokument wurde gespeichert:\n{document.FilePath}\n\nJetzt öffnen?",
                "PDF erstellt", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(document.FilePath) { UseShellExecute = true });
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "PDF konnte nicht erzeugt werden: " + exception.Message;
        }
    }

    private void OnSendMailClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastPdfPath))
        {
            InfoText.Text = "Bitte zuerst ein PDF erzeugen.";
            return;
        }

        var template = (TemplateBox.SelectedItem as TemplateListItem)?.Name ?? "Dokument";
        var dialog = new MailWindow(_services, _lastPdfPath, template) { Owner = this };
        dialog.ShowDialog();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
