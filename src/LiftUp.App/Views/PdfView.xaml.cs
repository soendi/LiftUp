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

    public PdfView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        FormatBox.ItemsSource = Formats;
        FormatBox.SelectedIndex = 0;
        OrientationBox.ItemsSource = Orientations;
        OrientationBox.SelectedIndex = 0;

        LoadTypes();
        LoadProjects();
        LoadTemplates();
        LoadDocuments();
    }

    private void LoadProjects()
    {
        try
        {
            var projects = _services.Projects.Search(new ProjectQuery())
                .Select(item => new ProjectOption(item.Id, $"{item.Number} · {item.Name}"))
                .ToArray();

            ProjectBox.ItemsSource = projects;
            ProjectBox.SelectedIndex = projects.Length > 0 ? 0 : -1;

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

    private void OnProjectChanged(object sender, SelectionChangedEventArgs e) => LoadDocuments();

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

    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (ProjectBox.SelectedItem is not ProjectOption project ||
            TemplateBox.SelectedItem is not TemplateListItem template)
        {
            InfoText.Text = "Bitte Projekt und Vorlage wählen.";
            return;
        }

        try
        {
            var html = _services.Documents.RenderHtml(project.Id, template.Id);
            await PreviewBrowser.EnsureCoreWebView2Async();
            PreviewBrowser.NavigateToString(html);
            InfoText.Text = $"Vorschau: {template.Name} (Version {template.CurrentVersion})";
        }
        catch (Exception exception)
        {
            InfoText.Text = "Vorschau fehlgeschlagen: " + exception.Message;
        }
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
