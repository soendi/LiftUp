using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class PdfView : UserControl
{
    private sealed record ProjectOption(Guid Id, string DisplayText);

    private sealed record TypeOption(string? Name, string Display);

    private sealed record FormatOption(string Value, string Name);

    private sealed record OrientationOption(bool Landscape, string Name);

    private static readonly FormatOption[] Formats = [new("A4", "A4"), new("A3", "A3")];
    private static readonly OrientationOption[] Orientations = [new(false, "Hochformat"), new(true, "Querformat")];

    private readonly AppServices _services;
    private readonly ProjectSelection _selection;
    private MissingPlaceholders _missing = MissingPlaceholders.Empty;
    private bool _previewShown;

    public PdfView(AppServices services, ProjectSelection selection)
    {
        _services = services;
        _selection = selection;
        InitializeComponent();

        FormatBox.ItemsSource = Formats;
        FormatBox.SelectedIndex = 0;
        OrientationBox.ItemsSource = Orientations;
        OrientationBox.SelectedIndex = 0;

        LoadTypes();
        LoadProjects();
        LoadTemplates();
        LoadDocuments();

        // Ein Projektwechsel in einem anderen Reiter zieht das PDF sofort mit.
        _selection.Changed += (_, _) => ShowSelectedProject();
        ShowSelectedProject();

        // Neu angelegte Projekte stehen sofort zur Auswahl, sobald der Reiter geöffnet wird.
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                LoadProjects(keepSelection: true);
            }
        };
    }

    private void LoadProjects(bool keepSelection = false)
    {
        var previousId = keepSelection
            ? (ProjectBox.SelectedItem as ProjectOption)?.Id
            : _selection.ProjectId;

        try
        {
            var projects = _services.Projects.Search(new ProjectQuery())
                .Select(item => new ProjectOption(item.Id, $"{item.Number} · {item.Name}"))
                .ToArray();

            var index = projects.Length > 0 ? 0 : -1;
            if (previousId is { } id)
            {
                var found = Array.FindIndex(projects, item => item.Id == id);
                if (found >= 0)
                {
                    index = found;
                }
            }

            ProjectBox.ItemsSource = projects;
            ProjectBox.SelectedIndex = index;

            InfoText.Text = projects.Length == 0
                ? "Noch keine Projekte vorhanden."
                : string.Empty;
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void LoadTypes()
    {
        var options = new List<TypeOption> { new(null, "Alle Typen") };

        try
        {
            options.AddRange(_services.DocumentTypes.GetAll().Select(type => new TypeOption(type.Name, type.Name)));
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }

        TypeFilterBox.ItemsSource = options;
        TypeFilterBox.SelectedIndex = 0;
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e) => LoadTemplates();

    private void OnTemplateChanged(object sender, SelectionChangedEventArgs e)
    {
        _previewShown = false;
        UpdateMissing();
    }

    private void LoadTemplates()
    {
        try
        {
            var documentType = (TypeFilterBox.SelectedItem as TypeOption)?.Name;
            var templates = _services.Templates.GetAll(includeInactive: false, documentType: documentType);
            TemplateBox.ItemsSource = templates;
            TemplateBox.SelectedIndex = templates.Count > 0 ? 0 : -1;

            if (templates.Count == 0)
            {
                InfoText.Text = documentType is null
                    ? "Keine aktive Vorlage vorhanden. Bitte unter „Vorlagen“ eine anlegen."
                    : $"Keine aktive Vorlage für den Typ „{documentType}“.";
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void LoadDocuments()
    {
        if (ProjectBox.SelectedItem is not ProjectOption project)
        {
            DocumentsGrid.ItemsSource = null;
            return;
        }

        try
        {
            DocumentsGrid.ItemsSource = _services.Documents.GetDocuments(project.Id);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private ProjectDocument? SelectedDocument => DocumentsGrid.SelectedItem as ProjectDocument;

    private void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadDocuments();
        UpdateMissing();

        // Die Auswahl gilt für alle Reiter (Zustandsanalyse, Investitionsplanung, Analyse, PDF).
        if (ProjectBox.SelectedItem is ProjectOption project)
        {
            _selection.Select(project.Id);
        }
    }

    /// <summary>Übernimmt das gemeinsame Projekt; ohne Auswahl gilt das erste Projekt.</summary>
    private void ShowSelectedProject()
    {
        if (ProjectBox.ItemsSource is not IEnumerable<ProjectOption> options)
        {
            return;
        }

        var all = options.ToArray();
        if (all.Length == 0)
        {
            return;
        }

        var target = all.FirstOrDefault(option => option.Id == _selection.ProjectId) ?? all[0];
        if (!ReferenceEquals(ProjectBox.SelectedItem, target))
        {
            ProjectBox.SelectedItem = target;
            return;
        }

        _selection.Select(target.Id);
    }

    private void OnDocumentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedDocument;
        OpenButton.IsEnabled = selected is not null && selected.FileExists;
        FolderButton.IsEnabled = selected is not null;
        EditDocumentButton.IsEnabled = selected is not null && selected.HasSections;
        SendMailButton.IsEnabled = selected is not null && selected.FileExists;
        DeleteDocumentButton.IsEnabled = selected is not null;
    }

    /// <summary>
    /// Entfernt ein erzeugtes Dokument. Die PDF-Datei landet unter Windows im Papierkorb,
    /// der der Eintrag verschwindet aus der Liste.
    /// </summary>
    private void OnDeleteDocumentClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDocument;
        if (selected is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Dokument \"{selected.FileName}\" wirklich löschen?\n\n" +
            "Der Eintrag wird entfernt, die PDF-Datei in den Papierkorb gelegt.",
            "Dokument löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var warning = _services.Documents.DeleteDocument(selected.Id);
            LoadDocuments();
            InfoText.Text = warning ?? $"Gelöscht: {selected.FileName}";

            if (warning is not null)
            {
                MessageBox.Show(warning, "Dokument löschen", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Löschen fehlgeschlagen: " + exception.Message;
        }
    }

    /// <summary>Öffnet ein früheres Dokument erneut, um die Abschnitte zu ändern.</summary>
    private void OnEditDocumentClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDocument;
        if (selected is null)
        {
            return;
        }

        if (!selected.HasSections)
        {
            InfoText.Text = "Dieses Dokument wurde ohne Abschnitte erzeugt und lässt sich nicht erneut bearbeiten.";
            return;
        }

        var dialog = new DocumentComposerWindow(
            _services,
            selected.ProjectId,
            selected.TemplateId,
            DocumentSections.Deserialize(selected.SectionsJson))
        {
            Owner = Window.GetWindow(this),
        };

        dialog.ShowDialog();
        LoadDocuments();
    }

    /// <summary>Versendet ein erzeugtes PDF per E-Mail.</summary>
    private void OnSendMailClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDocument;
        if (selected is null || !selected.FileExists)
        {
            InfoText.Text = "Die Datei ist nicht mehr vorhanden.";
            return;
        }

        var dialog = new MailWindow(_services, selected.FilePath, $"{selected.TemplateName} {selected.FileName}")
        {
            Owner = Window.GetWindow(this),
        };

        dialog.ShowDialog();
    }

    /// <summary>Öffnet den Editor, in dem die Abschnitte der Vorlage ausgefüllt werden.</summary>
    private void OnComposeClick(object sender, RoutedEventArgs e)
    {
        var project = ProjectBox.SelectedItem as ProjectOption;
        var template = TemplateBox.SelectedItem as TemplateListItem;

        var dialog = new DocumentComposerWindow(_services, project?.Id, template?.Id)
        {
            Owner = Window.GetWindow(this),
        };

        dialog.ShowDialog();
        LoadDocuments();
    }

    private async void OnPreviewClick(object sender, RoutedEventArgs e) => await ShowPreviewAsync();

    private async Task<bool> ShowPreviewAsync()
    {
        if (ProjectBox.SelectedItem is not ProjectOption project ||
            TemplateBox.SelectedItem is not TemplateListItem template)
        {
            InfoText.Text = "Bitte Projekt und Vorlage wählen.";
            return false;
        }

        UpdateMissing();

        try
        {
            var html = _services.Documents.RenderHtml(project.Id, template.Id);
            await PreviewBrowser.EnsureCoreWebView2Async();
            PreviewBrowser.NavigateToString(html);
            _previewShown = true;
            InfoText.Text = _missing.Any
                ? $"Vorschau: {template.Name} (Version {template.CurrentVersion}) – Angaben fehlen, siehe Hinweis."
                : $"Vorschau: {template.Name} (Version {template.CurrentVersion})";
            return true;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Vorschau fehlgeschlagen: " + exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Prüft die gewählte Vorlage auf fehlende Bilder und Textbausteine und zeigt die
    /// Warnung über der Vorschau – noch bevor jemand ein PDF erzeugt.
    /// </summary>
    private void UpdateMissing()
    {
        if (ProjectBox.SelectedItem is not ProjectOption project ||
            TemplateBox.SelectedItem is not TemplateListItem template)
        {
            _missing = MissingPlaceholders.Empty;
            WarningBorder.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            _missing = _services.Documents.FindMissingPlaceholders(project.Id, template.Id);
        }
        catch (LiftUpException)
        {
            // Ohne Recht oder ohne lesbare Vorlage gibt es hier nichts zu warnen.
            _missing = MissingPlaceholders.Empty;
            WarningBorder.Visibility = Visibility.Collapsed;
            return;
        }

        if (!_missing.Any)
        {
            WarningBorder.Visibility = Visibility.Collapsed;
            return;
        }

        WarningText.Text = $"Fehlende Angaben: {DescribeMissing(_missing)}. "
            + "Diese Platzhalter bleiben in der Vorschau und im erzeugten PDF leer.";
        WarningBorder.Visibility = Visibility.Visible;
    }

    private static string DescribeMissing(MissingPlaceholders missing)
    {
        var parts = new List<string>();
        if (missing.Images.Count > 0)
        {
            parts.Add($"{missing.Images.Count} Bild{(missing.Images.Count == 1 ? "" : "er")}");
        }

        if (missing.Snippets.Count > 0)
        {
            parts.Add($"{missing.Snippets.Count} Textbaustein{(missing.Snippets.Count == 1 ? "" : "e")}");
        }

        var names = string.Join(", ", missing.Images.Concat(missing.Snippets)
            .Select(name => $"„{name}“"));

        return $"{string.Join(" und ", parts)} ({names})";
    }

    private async void OnFixPlaceholdersClick(object sender, RoutedEventArgs e)
    {
        if (ProjectBox.SelectedItem is not ProjectOption project ||
            TemplateBox.SelectedItem is not TemplateListItem template)
        {
            return;
        }

        var dialog = new MissingPlaceholdersWindow(_services, project.Id, template.Id, _missing)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true)
        {
            UpdateMissing();
            return;
        }

        UpdateMissing();

        if (_previewShown)
        {
            await ShowPreviewAsync();
        }

        InfoText.Text = dialog.PlaceholdersRemoved
            ? "Platzhalter entfernt – Vorschau und PDF sind wieder vollständig nutzbar."
            : "Angaben ergänzt – Vorschau und PDF sind wieder vollständig nutzbar.";
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (ProjectBox.SelectedItem is not ProjectOption project ||
            TemplateBox.SelectedItem is not TemplateListItem template)
        {
            InfoText.Text = "Bitte Projekt und Vorlage wählen.";
            return;
        }

        var format = (FormatBox.SelectedItem as FormatOption)?.Value ?? "A4";
        var landscape = (OrientationBox.SelectedItem as OrientationOption)?.Landscape ?? false;
        var margin = double.TryParse(MarginBox.Text, out var parsed) ? parsed : 15;

        try
        {
            InfoText.Text = "PDF wird erzeugt …";
            var document = await _services.Documents.CreatePdfAsync(
                project.Id,
                template.Id,
                new PdfPageSettings(format, landscape, margin));

            LoadDocuments();
            InfoText.Text = $"Erstellt: {document.FileName} (Vorlage {template.Name}, Version {document.TemplateVersion})";
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

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDocument;
        if (selected is null || !selected.FileExists)
        {
            InfoText.Text = "Die Datei ist nicht mehr vorhanden.";
            return;
        }

        Process.Start(new ProcessStartInfo(selected.FilePath) { UseShellExecute = true });
    }

    private void OnShowFolderClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDocument;
        if (selected is null)
        {
            return;
        }

        if (File.Exists(selected.FilePath))
        {
            Process.Start("explorer.exe", $"/select,\"{selected.FilePath}\"");
            return;
        }

        var folder = Path.GetDirectoryName(selected.FilePath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
    }
}
