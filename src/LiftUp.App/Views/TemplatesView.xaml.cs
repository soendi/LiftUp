using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiftUp.App.Documents;
using LiftUp.Core.Documents;
using Microsoft.Win32;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class TemplatesView : UserControl
{
    /// <summary>Ein Blatt der Vorlage: Untertitel und Startinhalt (HTML) in einem eigenen Reiter.</summary>
    private sealed class SectionRow : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _html = string.Empty;

        public string Title
        {
            get => _title;
            set
            {
                if (_title == value)
                {
                    return;
                }

                _title = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            }
        }

        public string Html
        {
            get => _html;
            set
            {
                if (_html == value)
                {
                    return;
                }

                _html = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Html)));
            }
        }

        /// <summary>Reiter des Blatts; entsteht beim Aufbau der Reiterleiste.</summary>
        public TabItem? Tab { get; set; }

        /// <summary>Codefeld des Blatts – Ziel für Platzhalter aus der Liste.</summary>
        public TextBox? Editor { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Ein Treffer der „&amp;“-Suche im Vorlagentext.</summary>
    private sealed record ImageChoice(string Name, string Placeholder, string Detail, BitmapImage? Preview);

    private static readonly Regex ImageTokenPattern =
        new(@"(^|[\s\u00a0>])(&{1,2})([^\s&]{0,40})$", RegexOptions.Compiled);

    private readonly AppServices _services;
    private readonly ObservableCollection<SectionRow> _sectionRows = [];
    private readonly DispatcherTimer _missingTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private IReadOnlyList<ImageChoice> _imageMatches = [];
    private int _imageTokenStart;
    private int _imageTokenLength;
    private bool _insertingImage;
    private Guid? _templateId;
    private IReadOnlyList<string> _missingImages = [];
    private IReadOnlyList<string> _missingSnippets = [];

    public TemplatesView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        PlaceholderList.ItemsSource = PlaceholderRenderer.Known
            .Select(entry => new { entry.Token, entry.Description })
            .ToArray();

        // Die Bildsuche hängt am Textfeld: Nach „&“ erscheint eine Liste mit Vorschau.
        HtmlBox.TextChanged += OnHtmlBoxTextChanged;
        HtmlBox.PreviewKeyDown += OnHtmlBoxPreviewKeyDown;

        // Fehlende Bilder und Textbausteine schon beim Bearbeiten markieren; die kurze
        // Verzögerung erspart die Prüfung bei jedem Tastendruck.
        _missingTimer.Tick += (_, _) =>
        {
            _missingTimer.Stop();
            UpdateMissingPlaceholders();
        };
        HtmlBox.TextChanged += (_, _) => ScheduleMissingCheck();

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

            // Erst die alten Reiter entfernen, dann die Liste leeren: Sonst bleiben die Blätter
            // der zuvor geöffneten Vorlage stehen und jedes Blatt erscheint doppelt.
            RemoveSectionTabs();
            _sectionRows.Clear();
            foreach (var section in _services.Templates.GetSections(templateId))
            {
                _sectionRows.Add(new SectionRow { Title = section.Title, Html = section.DefaultHtml });
            }

            RebuildSectionTabs();

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
        RemoveSectionTabs();
        _sectionRows.Clear();
        RebuildSectionTabs();
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

        var html = PlaceholderRenderer.Render(ActiveEditor.Text, new PlaceholderContext
        {
            Project = sample,
            UserName = _services.CurrentSession.DisplayName,
        });

        try
        {
            await PreviewBrowser.EnsureCoreWebView2Async();
            PreviewBrowser.NavigateToString(html);
            InfoText.Text = $"Vorschau von „{ActiveSheetName}“ mit Beispieldaten.";
        }
        catch (Exception exception)
        {
            InfoText.Text = "Vorschau fehlgeschlagen: " + exception.Message;
        }
    }

    /// <summary>Das gewählte Blatt: Deckblatt oder ein Blatt mit eigenem Code.</summary>
    private SectionRow? SelectedSection =>
        SheetTabs.SelectedItem is TabItem { Tag: SectionRow row } ? row : null;

    /// <summary>Codefeld, in das Platzhalter und Vorschau gehen: das gerade gewählte Blatt.</summary>
    private TextBox ActiveEditor => SelectedSection?.Editor ?? HtmlBox;

    private string ActiveSheetName => SelectedSection is { Title.Length: > 0 } row ? row.Title : "Deckblatt";

    /// <summary>Entfernt die Reiter der Blätter, die gerade in der Liste stehen.</summary>
    private void RemoveSectionTabs()
    {
        foreach (var row in _sectionRows)
        {
            if (row.Tab is not null)
            {
                SheetTabs.Items.Remove(row.Tab);
                row.Tab = null;
                row.Editor = null;
            }
        }
    }

    /// <summary>Legt je Blatt einen Reiter an; „Deckblatt“ bleibt der erste Reiter.</summary>
    private void RebuildSectionTabs()
    {
        RemoveSectionTabs();

        foreach (var row in _sectionRows)
        {
            AddSectionTab(row);
        }

        UpdateSheetHeaders();
        UpdateSheetButtons();
    }

    /// <summary>Reiter eines Blatts: Untertitel und HTML, beides direkt an die Zeile gebunden.</summary>
    private void AddSectionTab(SectionRow row)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock
        {
            Text = "Untertitel",
            Style = (Style)FindResource("LiftUpFieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        var titleBox = new TextBox
        {
            Width = 420,
            ToolTip = "Titel des Blatts, etwa „1. Anlagedaten“",
        };
        titleBox.SetBinding(TextBox.TextProperty, new Binding(nameof(SectionRow.Title))
        {
            Source = row,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        titleBox.TextChanged += (_, _) => UpdateSheetHeaders();
        header.Children.Add(titleBox);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 240,
            Margin = new Thickness(2),
        };
        editor.SetBinding(TextBox.TextProperty, new Binding(nameof(SectionRow.Html))
        {
            Source = row,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        Grid.SetRow(editor, 1);
        grid.Children.Add(editor);

        var tab = new TabItem { Content = grid, Tag = row };
        row.Tab = tab;
        row.Editor = editor;
        SheetTabs.Items.Add(tab);
    }

    /// <summary>Der Reiter trägt den Untertitel des Blatts; ohne Untertitel „Blatt n“.</summary>
    private void UpdateSheetHeaders()
    {
        for (var index = 0; index < _sectionRows.Count; index++)
        {
            var row = _sectionRows[index];
            if (row.Tab is null)
            {
                continue;
            }

            var header = row.Title.Length > 0 ? row.Title : $"Blatt {index + 1}";
            row.Tab.Header = header;
            row.Tab.ToolTip = header;
        }
    }

    private void OnSheetTabChanged(object sender, SelectionChangedEventArgs e) => UpdateSheetButtons();

    /// <summary>„Entfernen“ und Verschieben gilt nur für Blätter, nicht fürs Deckblatt.</summary>
    private void UpdateSheetButtons()
    {
        var row = SelectedSection;
        var index = row is null ? -1 : _sectionRows.IndexOf(row);
        RemoveSectionButton.IsEnabled = index >= 0;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < _sectionRows.Count - 1;
    }

    private void OnAddSectionClick(object sender, RoutedEventArgs e)
    {
        var row = new SectionRow();
        _sectionRows.Add(row);
        AddSectionTab(row);
        UpdateSheetHeaders();
        SheetTabs.SelectedItem = row.Tab;
        UpdateSheetButtons();

        // Der Reiterinhalt entsteht erst beim nächsten Layoutlauf – Fokus deshalb später setzen.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (row.Tab?.Content is Grid { Children.Count: > 0 } content
                && content.Children[0] is StackPanel { Children.Count: > 1 } panel
                && panel.Children[1] is TextBox box)
            {
                box.Focus();
                box.SelectAll();
            }
        }));

        InfoText.Text = "Blatt angelegt – Untertitel und Inhalt erfassen, dann „Blätter speichern“.";
    }

    private void OnRemoveSectionClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSection is not { } row)
        {
            return;
        }

        var index = _sectionRows.IndexOf(row);
        var name = row.Title.Length > 0 ? row.Title : $"Blatt {index + 1}";
        var answer = MessageBox.Show(
            $"Blatt „{name}“ entfernen?\n\nÜbernommen wird das mit „Blätter speichern“; "
            + "bereits erzeugte PDFs bleiben unverändert.",
            "Blatt entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        RemoveSection(row);
    }

    private void RemoveSection(SectionRow row)
    {
        var index = _sectionRows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        _sectionRows.RemoveAt(index);
        if (row.Tab is not null)
        {
            SheetTabs.Items.Remove(row.Tab);
        }

        UpdateSheetHeaders();

        var next = _sectionRows.Count == 0
            ? null
            : _sectionRows[Math.Min(index, _sectionRows.Count - 1)];

        if (next?.Tab is { } tab)
        {
            SheetTabs.SelectedItem = tab;
        }
        else
        {
            SheetTabs.SelectedItem = CoverTab;
        }

        UpdateSheetButtons();
        InfoText.Text = "Blatt entfernt – noch „Blätter speichern“.";
    }

    private void OnMoveSectionUpClick(object sender, RoutedEventArgs e) => MoveSection(-1);

    private void OnMoveSectionDownClick(object sender, RoutedEventArgs e) => MoveSection(1);

    private void MoveSection(int offset)
    {
        if (SelectedSection is not { } row)
        {
            return;
        }

        var index = _sectionRows.IndexOf(row);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _sectionRows.Count)
        {
            return;
        }

        _sectionRows.Move(index, target);
        RebuildSectionTabs();
        SheetTabs.SelectedItem = row.Tab;
        UpdateSheetButtons();
    }

    private void OnSaveSectionsClick(object sender, RoutedEventArgs e)
    {
        if (_templateId is null)
        {
            InfoText.Text = "Bitte zuerst die Vorlage speichern, dann Blätter festlegen.";
            return;
        }

        try
        {
            _services.Templates.SaveSections(
                _templateId.Value,
                _sectionRows.Select(row => (row.Title, row.Html)).ToArray());
            InfoText.Text = $"Blätter gespeichert ({_sectionRows.Count(row => !string.IsNullOrWhiteSpace(row.Title))}).";
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

    private void ScheduleMissingCheck()
    {
        _missingTimer.Stop();
        _missingTimer.Start();
    }

    /// <summary>
    /// Prüft den Text im Editor auf Bilder und Textbausteine, die es nicht (mehr) gibt –
    /// noch bevor die Vorlage gespeichert oder ein Dokument erzeugt wird. Vorlagen hängen
    /// an keinem Projekt, deshalb zählt hier die gesamte Bildbibliothek.
    /// </summary>
    private void UpdateMissingPlaceholders()
    {
        _missingImages = [];
        _missingSnippets = [];

        var imageNames = PlaceholderRenderer.FindImageNames(HtmlBox.Text);
        var snippetNames = PlaceholderRenderer.FindSnippetNames(HtmlBox.Text);

        if (imageNames.Count == 0 && snippetNames.Count == 0)
        {
            MissingBorder.Visibility = Visibility.Collapsed;
            return;
        }

        HashSet<string> knownImages;
        HashSet<string> knownSnippets;
        try
        {
            knownImages = _services.Images.Search(null, null, allProjects: true)
                .Where(image => !image.IsArchived && File.Exists(image.FilePath))
                .Select(image => image.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            knownSnippets = _services.Snippets.GetAll()
                .Select(snippet => snippet.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (LiftUpException)
        {
            MissingBorder.Visibility = Visibility.Collapsed;
            return;
        }

        _missingImages = imageNames.Where(name => !knownImages.Contains(name)).ToArray();
        _missingSnippets = snippetNames.Where(name => !knownSnippets.Contains(name)).ToArray();

        if (_missingImages.Count == 0 && _missingSnippets.Count == 0)
        {
            MissingBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>();
        if (_missingImages.Count > 0)
        {
            parts.Add($"{_missingImages.Count} Bild{(_missingImages.Count == 1 ? "" : "er")}");
        }

        if (_missingSnippets.Count > 0)
        {
            parts.Add($"{_missingSnippets.Count} Textbaustein{(_missingSnippets.Count == 1 ? "" : "e")}");
        }

        var details = new List<string>();
        if (_missingImages.Count > 0)
        {
            details.Add("Bilder: " + string.Join(", ", _missingImages.Select(name => $"„{name}“")));
        }

        if (_missingSnippets.Count > 0)
        {
            details.Add("Textbausteine: " + string.Join(", ", _missingSnippets.Select(name => $"„{name}“")));
        }

        MissingTitle.Text = $"Fehlende Angaben: {string.Join(" und ", parts)}";
        MissingText.Text = string.Join(" · ", details)
            + " – diese Stellen bleiben in der Vorschau und im erzeugten PDF leer.";
        ReplaceImagesButton.Visibility = _missingImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MissingBorder.Visibility = Visibility.Visible;
    }

    /// <summary>Entfernt die fehlenden Platzhalter direkt im Editor; gespeichert wird danach wie gewohnt.</summary>
    private void OnRemoveMissingClick(object sender, RoutedEventArgs e)
    {
        if (_missingImages.Count == 0 && _missingSnippets.Count == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            "Alle fehlenden Platzhalter aus dem Vorlagentext entfernen?\n\n"
            + "Das betrifft nur den Editor; übernommen wird die Änderung mit „Als neue Version speichern“. "
            + "Bereits erzeugte PDFs bleiben unverändert.",
            "Platzhalter entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        HtmlBox.Text = PlaceholderRenderer.RemovePlaceholders(HtmlBox.Text, _missingImages, _missingSnippets);
        HtmlBox.CaretIndex = HtmlBox.Text.Length;
        UpdateMissingPlaceholders();
        InfoText.Text = "Platzhalter entfernt – noch als neue Version speichern.";
    }

    /// <summary>Holt fehlende Bilder aus einer Datei in die globale Bibliothek.</summary>
    private void OnReplaceImagesClick(object sender, RoutedEventArgs e)
    {
        if (_missingImages.Count == 0)
        {
            return;
        }

        var dialog = new ReplaceImagesWindow(_services, _missingImages)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        UpdateMissingPlaceholders();
        InfoText.Text = "Bilder ersetzt – die neue Vorlagenversion entsteht beim Speichern.";
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

        var editor = ActiveEditor;
        var index = editor.CaretIndex;
        editor.Text = editor.Text.Insert(index, token);
        editor.CaretIndex = index + token.Length;
        editor.Focus();
    }
}
