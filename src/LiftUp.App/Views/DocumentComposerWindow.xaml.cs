using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LiftUp.App.Controls;
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

    public DocumentComposerWindow(AppServices services, Guid? projectId = null, Guid? templateId = null)
    {
        _services = services;
        _presetProjectId = projectId;
        _presetTemplateId = templateId;

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

        InfoText.Text = $"{template.Name}: {defined.Count} Abschnitt(e), jeder beginnt im PDF auf einer neuen Seite.";
    }

    private void AddSection(string title)
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

        var insertImage = new Button { Content = "Bild einfügen", Margin = new Thickness(0) };
        insertImage.Click += OnInsertImageClick;

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        buttonRow.Children.Add(insertSnippet);
        buttonRow.Children.Add(insertImage);

        var editor = new HtmlEditor();

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

    private async void OnCreatePdfClick(object sender, RoutedEventArgs e)
    {
        var project = SelectedProject;
        if (project is null || TemplateBox.SelectedItem is not TemplateListItem template)
        {
            InfoText.Text = "Bitte Projekt und Vorlage wählen.";
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

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
