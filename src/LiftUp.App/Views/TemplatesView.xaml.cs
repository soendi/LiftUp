using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LiftUp.App.Documents;
using LiftUp.Core.Documents;
using Microsoft.Win32;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class TemplatesView : UserControl
{
    /// <summary>Bearbeitbare Zeile der Abschnittsliste.</summary>
    private sealed class SectionRow
    {
        public string Title { get; set; } = string.Empty;
    }

    /// <summary>Ein Treffer der „&amp;“-Suche im Vorlagentext.</summary>
    private sealed record ImageChoice(string Name, string Placeholder, string Detail, BitmapImage? Preview);

    private static readonly Regex ImageTokenPattern =
        new(@"(^|[\s\u00a0>])(&{1,2})([^\s&]{0,40})$", RegexOptions.Compiled);

    private readonly AppServices _services;
    private readonly ObservableCollection<SectionRow> _sectionRows = [];
    private IReadOnlyList<ImageChoice> _imageMatches = [];
    private int _imageTokenStart;
    private int _imageTokenLength;
    private bool _insertingImage;
    private Guid? _templateId;

    public TemplatesView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        PlaceholderList.ItemsSource = PlaceholderRenderer.Known
            .Select(entry => new { entry.Token, entry.Description })
            .ToArray();

        SectionsList.ItemsSource = _sectionRows;

        // Die Bildsuche hängt am Textfeld: Nach „&“ erscheint eine Liste mit Vorschau.
        HtmlBox.TextChanged += OnHtmlBoxTextChanged;
        HtmlBox.PreviewKeyDown += OnHtmlBoxPreviewKeyDown;

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
            DeleteButton.IsEnabled = true;
            ToggleActiveButton.Content = template.IsActive ? "Deaktivieren" : "Aktivieren";

            var versions = _services.Templates.GetVersions(templateId);
            VersionBox.ItemsSource = versions;
            VersionBox.SelectedIndex = 0;
            HtmlBox.Text = versions.Count > 0 ? versions[0].Html : string.Empty;

            _sectionRows.Clear();
            foreach (var section in _services.Templates.GetSections(templateId))
            {
                _sectionRows.Add(new SectionRow { Title = section.Title });
            }

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

    /// <summary>
    /// Übernimmt eine vorhandene Datei als neue Vorlage. Der Inhalt landet im
    /// Editor, damit Struktur und Text vor dem Speichern geprüft werden können.
    /// </summary>
    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Vorlage aus Datei importieren",
            Filter =
                "Unterstützte Dateien|*.xlsx;*.csv;*.html;*.htm;*.txt;*.docx|" +
                "Excel-Arbeitsmappe|*.xlsx|" +
                "CSV-Datei|*.csv|" +
                "HTML-Datei|*.html;*.htm|" +
                "Textdatei|*.txt|" +
                "Word-Dokument|*.docx",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var imported = FileTemplateImporter.Import(dialog.FileName);

            _templateId = null;
            TemplatesGrid.SelectedItem = null;
            VersionBox.ItemsSource = null;
            ToggleActiveButton.IsEnabled = false;
            _sectionRows.Clear();

            NameBox.Text = imported.SuggestedName;
            TypeBox.SelectedItem = (TypeBox.ItemsSource as IEnumerable<DocumentType>)?
                .FirstOrDefault(type => type.Name == "Dokument");
            HtmlBox.Text = imported.Html;

            InfoText.Text =
                $"{imported.SourceKind}-Datei übernommen. Bitte prüfen und mit „Als neue Version speichern“ als Vorlage anlegen.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Import fehlgeschlagen: " + exception.Message;
        }
    }

    private void ClearEditor()
    {
        NameBox.Text = string.Empty;
        TypeBox.SelectedItem = null;
        HtmlBox.Text = string.Empty;
        VersionBox.ItemsSource = null;
        ToggleActiveButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        _sectionRows.Clear();
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
            CustomerStreet = "Hauptstraße 12",
            CustomerZip = "12345",
            CustomerCity = "Musterstadt",
            Contacts =
            [
                new ProjectContact
                {
                    Name = "Frau Beispiel",
                    Responsibility = "Bauleitung",
                    Phone = "0151 1234",
                    Email = "beispiel@example.com",
                },
            ],
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

    private void OnAddSectionClick(object sender, RoutedEventArgs e)
    {
        _sectionRows.Add(new SectionRow());
        SectionsList.SelectedIndex = _sectionRows.Count - 1;
    }

    private void OnRemoveSectionClick(object sender, RoutedEventArgs e)
    {
        if (SectionsList.SelectedItem is SectionRow row)
        {
            _sectionRows.Remove(row);
        }
    }

    private void OnMoveSectionUpClick(object sender, RoutedEventArgs e) => MoveSection(-1);

    private void OnMoveSectionDownClick(object sender, RoutedEventArgs e) => MoveSection(1);

    private void MoveSection(int offset)
    {
        if (SectionsList.SelectedItem is not SectionRow row)
        {
            return;
        }

        var index = _sectionRows.IndexOf(row);
        var target = index + offset;
        if (target < 0 || target >= _sectionRows.Count)
        {
            return;
        }

        _sectionRows.Move(index, target);
        SectionsList.SelectedItem = row;
    }

    private void OnSaveSectionsClick(object sender, RoutedEventArgs e)
    {
        if (_templateId is null)
        {
            InfoText.Text = "Bitte zuerst die Vorlage speichern, dann Abschnitte festlegen.";
            return;
        }

        try
        {
            _services.Templates.SaveSections(_templateId.Value, _sectionRows.Select(row => row.Title).ToArray());
            InfoText.Text = $"Abschnitte gespeichert ({_sectionRows.Count(row => !string.IsNullOrWhiteSpace(row.Title))}).";
            LoadTemplate(_templateId.Value);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Abschnitte konnten nicht gespeichert werden: " + exception.Message;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_templateId is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Vorlage \"{NameBox.Text}\" wirklich löschen?\n\n" +
            "Die Vorlage verschwindet aus der Auswahl. Bereits erzeugte PDFs bleiben erhalten.",
            "Vorlage löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Templates.Delete(_templateId.Value);
            _templateId = null;
            ClearEditor();
            LoadTemplates();
            InfoText.Text = "Vorlage gelöscht.";
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

    /// <summary>
    /// Sucht nach „&amp;“ im Vorlagentext nach Bildern. Vorlagen hängen an keinem Projekt,
    /// deshalb durchsuchen „&amp;“ und „&amp;&amp;“ alle Bilder. Eingefügt wird der Platzhalter
    /// <c>{{bild:Name}}</c>, damit die Vorlage klein bleibt und für jedes Projekt passt.
    /// </summary>
    private void OnHtmlBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_insertingImage)
        {
            return;
        }

        var caret = Math.Clamp(HtmlBox.CaretIndex, 0, HtmlBox.Text.Length);
        var before = HtmlBox.Text[..caret];
        var match = ImageTokenPattern.Match(before);

        if (!match.Success)
        {
            CloseImagePopup();
            return;
        }

        var prefixLength = match.Groups[2].Value.Length;
        var query = match.Groups[3].Value;
        _imageTokenStart = caret - prefixLength - query.Length;
        _imageTokenLength = prefixLength + query.Length;

        var matches = SearchImages(query);
        if (matches.Count == 0)
        {
            CloseImagePopup();
            InfoText.Text = query.Length == 0
                ? "Keine Bilder vorhanden. Bilder unter „Bilder“ erfassen oder im Projektordner ablegen."
                : $"Kein Bild zu \"{query}\" gefunden.";
            return;
        }

        _imageMatches = matches;
        ImageList.ItemsSource = matches;
        ImageList.SelectedIndex = 0;

        var caretRect = HtmlBox.GetRectFromCharacterIndex(Math.Max(_imageTokenStart, 0), trailingEdge: false);
        ImagePopup.HorizontalOffset = caretRect.Left;
        ImagePopup.VerticalOffset = caretRect.Bottom + 2;
        ImagePopup.IsOpen = true;
    }

    private IReadOnlyList<ImageChoice> SearchImages(string query)
    {
        try
        {
            return _services.Images
                .Search(null, query, allProjects: true)
                .Take(8)
                .Select(image => new ImageChoice(
                    image.Name,
                    $"{{{{bild:{image.Name}}}}}",
                    BuildDetail(image),
                    ImagePreviews.Load(image.FilePath)))
                .ToArray();
        }
        catch (PermissionDeniedException)
        {
            return [];
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
            return [];
        }
    }

    private static string BuildDetail(ImageListItem image) => string.Join(" · ",
        new[] { image.Category, image.Tags, image.ScopeText }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private void OnHtmlBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ImagePopup.IsOpen || _imageMatches.Count == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                ImageList.SelectedIndex = (ImageList.SelectedIndex + 1) % _imageMatches.Count;
                e.Handled = true;
                break;
            case Key.Up:
                ImageList.SelectedIndex = ImageList.SelectedIndex <= 0
                    ? _imageMatches.Count - 1
                    : ImageList.SelectedIndex - 1;
                e.Handled = true;
                break;
            case Key.Enter:
                if (ImageList.SelectedItem is ImageChoice choice)
                {
                    InsertImage(choice);
                }

                e.Handled = true;
                break;
            case Key.Escape:
                CloseImagePopup();
                e.Handled = true;
                break;
        }
    }

    private void OnImageListClick(object sender, MouseButtonEventArgs e)
    {
        if (ImageList.SelectedItem is ImageChoice choice)
        {
            InsertImage(choice);
        }
    }

    /// <summary>Ersetzt das getippte „&amp;…“ durch den Bild-Platzhalter.</summary>
    private void InsertImage(ImageChoice choice)
    {
        var start = Math.Clamp(_imageTokenStart, 0, HtmlBox.Text.Length);
        var length = Math.Clamp(_imageTokenLength, 0, HtmlBox.Text.Length - start);
        CloseImagePopup();

        _insertingImage = true;
        try
        {
            HtmlBox.Text = HtmlBox.Text.Remove(start, length).Insert(start, choice.Placeholder);
            HtmlBox.CaretIndex = start + choice.Placeholder.Length;
            HtmlBox.Focus();
        }
        finally
        {
            _insertingImage = false;
        }

        InfoText.Text = $"Platzhalter {choice.Placeholder} eingefügt – beim Erzeugen wird das Bild eingesetzt.";
    }

    private void CloseImagePopup()
    {
        ImagePopup.IsOpen = false;
        _imageMatches = [];
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
