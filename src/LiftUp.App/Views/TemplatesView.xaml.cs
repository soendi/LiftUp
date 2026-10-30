using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class TemplatesView : UserControl
{
    private readonly AppServices _services;
    private Guid? _templateId;

    public TemplatesView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        PlaceholderList.ItemsSource = PlaceholderRenderer.Known
            .Select(entry => new { entry.Token, entry.Description })
            .ToArray();

        LoadDocumentTypes();
        LoadTemplates();
    }

    private void LoadDocumentTypes()
    {
        try
        {
            TypeBox.ItemsSource = _services.DocumentTypes.GetAll();
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnManageTypesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new DocumentTypesWindow(_services) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();

        LoadDocumentTypes();
        LoadTemplates(_templateId);
    }

    private void LoadTemplates(Guid? selectId = null)
    {
        try
        {
            var templates = _services.Templates.GetAll();

            // Auswahl merken, damit nach dem Speichern dieselbe Vorlage aktiv bleibt.
            var target = selectId ?? _templateId;
            TemplatesGrid.ItemsSource = templates;

            var selected = templates.FirstOrDefault(item => item.Id == target) ?? templates.FirstOrDefault();
            TemplatesGrid.SelectedItem = selected;
            if (selected is not null)
            {
                LoadTemplate(selected.Id);
            }
            else
            {
                ClearEditor();
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnTemplateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TemplatesGrid.SelectedItem is TemplateListItem item)
        {
            LoadTemplate(item.Id);
        }
    }

    private void LoadTemplate(Guid templateId)
    {
        try
        {
            var template = _services.Templates.Get(templateId);
            _templateId = template.Id;

            NameBox.Text = template.Name;
            TypeBox.SelectedItem = (TypeBox.ItemsSource as IEnumerable<DocumentType>)?
                .FirstOrDefault(type => string.Equals(type.Name, template.DocumentType, StringComparison.OrdinalIgnoreCase));
            ToggleActiveButton.IsEnabled = true;
            ToggleActiveButton.Content = template.IsActive ? "Deaktivieren" : "Aktivieren";

            var versions = _services.Templates.GetVersions(templateId);
            VersionBox.ItemsSource = versions;
            VersionBox.SelectedIndex = 0;
            HtmlBox.Text = versions.Count > 0 ? versions[0].Html : string.Empty;

            InfoText.Text = $"Vorlage \"{template.Name}\", aktuelle Version {template.CurrentVersion}";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (VersionBox.SelectedItem is TemplateVersion version)
        {
            HtmlBox.Text = version.Html;
            InfoText.Text = $"Version {version.Version} vom {version.CreatedText} ({version.CreatedBy}) – Änderungen werden beim Speichern zur nächsten Version.";
        }
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        _templateId = null;
        TemplatesGrid.SelectedItem = null;
        ClearEditor();

        NameBox.Text = "Neue Vorlage";
        TypeBox.SelectedItem = (TypeBox.ItemsSource as IEnumerable<DocumentType>)?
            .FirstOrDefault(type => type.Name == "Dokument");
        HtmlBox.Text = """
            <!DOCTYPE html>
            <html lang="de">
            <head>
              <meta charset="utf-8" />
              <style>
                @page { size: A4; margin: 20mm 18mm; }
                body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 11pt; }
                h1 { font-size: 18pt; }
              </style>
            </head>
            <body>
              <h1>{{project.name}}</h1>
              <p>Projekt {{project.number}} · {{date}}</p>
              <p>Kunde: {{project.customer}}</p>
            </body>
            </html>
            """;

        NameBox.Focus();
        NameBox.SelectAll();
        InfoText.Text = "Neue Vorlage – mit „Als neue Version speichern“ anlegen.";
    }

    private void ClearEditor()
    {
        NameBox.Text = string.Empty;
        TypeBox.SelectedItem = null;
        HtmlBox.Text = string.Empty;
        VersionBox.ItemsSource = null;
        ToggleActiveButton.IsEnabled = false;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var input = new TemplateInput
        {
            Name = NameBox.Text,
            DocumentType = (TypeBox.SelectedItem as DocumentType)?.Name ?? string.Empty,
            Html = HtmlBox.Text,
            IsActive = true,
        };

        try
        {
            DocumentTemplate template;
            if (_templateId is null)
            {
                template = _services.Templates.Create(input);
            }
            else
            {
                template = _services.Templates.SaveNewVersion(_templateId.Value, input);
            }

            _templateId = template.Id;
            LoadTemplates(template.Id);
            InfoText.Text = $"Gespeichert als Version {template.CurrentVersion}. Frühere PDFs bleiben unverändert.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Speichern fehlgeschlagen: " + exception.Message;
        }
    }

    private void OnToggleActiveClick(object sender, RoutedEventArgs e)
    {
        if (_templateId is null)
        {
            return;
        }

        try
        {
            var template = _services.Templates.Get(_templateId.Value);
            _services.Templates.SetActive(template.Id, !template.IsActive);
            LoadTemplates(template.Id);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        var sample = new Project
        {
            Number = "2026-0001",
            Name = "Beispielprojekt",
            Customer = "Muster Bau GmbH",
            Contact = "Frau Beispiel",
            Description = "Beispielbeschreibung für die Vorschau.",
            ResponsibleName = _services.CurrentSession.DisplayName,
            Status = ProjectStatus.Active,
            FolderPath = @"D:\Projekte\Beispielprojekt",
        };

        var html = PlaceholderRenderer.Render(HtmlBox.Text, new PlaceholderContext
        {
            Project = sample,
            UserName = _services.CurrentSession.DisplayName,
        });

        try
        {
            await PreviewBrowser.EnsureCoreWebView2Async();
            PreviewBrowser.NavigateToString(html);
            InfoText.Text = "Vorschau mit Beispieldaten.";
        }
        catch (Exception exception)
        {
            InfoText.Text = "Vorschau fehlgeschlagen: " + exception.Message;
        }
    }

    private void OnPlaceholderDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PlaceholderList.SelectedItem is null)
        {
            return;
        }

        var token = PlaceholderList.SelectedItem.GetType().GetProperty("Token")?.GetValue(PlaceholderList.SelectedItem) as string;
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        var index = HtmlBox.CaretIndex;
        HtmlBox.Text = HtmlBox.Text.Insert(index, token);
        HtmlBox.CaretIndex = index + token.Length;
        HtmlBox.Focus();
    }
}
