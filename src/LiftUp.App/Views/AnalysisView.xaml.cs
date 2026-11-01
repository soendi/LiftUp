using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiftUp.App.Controls;
using LiftUp.App.Documents;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>
/// Erfasst die Analyse je Projekt: ein Haupttitel („2. Analyse“) mit einem Unter-Reiter
/// per Punkt (2.1 Aggregat, 2.2 Hubzylinder / Tragmittel …). Die Texte hängen am Projekt,
/// damit sie in mehreren Dokumenten wiederverwendet werden.
/// </summary>
public partial class AnalysisView : UserControl
{
    private sealed record ProjectOption(Guid Id, string DisplayText);

    private sealed record SectionDraft(ProjectAnalysisSection Section, HtmlEditor Editor)
    {
        /// <summary>Ob im Reiter etwas steht – bestimmt seine Farbe.</summary>
        public bool Filled { get; set; }
    }

    private readonly AppServices _services;
    private readonly ProjectSelection _selection;
    private readonly List<SectionDraft> _sections = [];

    /// <summary>Suche für „/“ (Textbausteine) und „&amp;“ / „&amp;&amp;“ (Bilder) in den Editoren.</summary>
    private readonly EditorSearch _search;

    /// <summary>Tabelle unter „1. Anlagedaten“ – je Aufzug eine Zeile, vorbelegt aus der Zustandsanalyse.</summary>
    private ProjectTableView? _stateTable;

    /// <summary>Unter-Reiter je Aufzug unter „4. Fotos“.</summary>
    private readonly TabControl _photoTabs = new();

    /// <summary>Alle Reiter mit ihrer Farbe.</summary>
    private readonly List<TabStatus> _tabStatuses = [];

    /// <summary>Die Foto-Reiter je Aufzug; „4. Fotos“ ist grün, sobald einer etwas hat.</summary>
    private readonly List<TabStatus> _photoTabStatuses = [];

    /// <summary>Farbe der Reiter: hellrot ohne Inhalt, hellgrün mit Inhalt.</summary>
    private static readonly SolidColorBrush MissingBrush = CreateBrush(0xF9, 0xD6, 0xD5);
    private static readonly SolidColorBrush DoneBrush = CreateBrush(0xD7, 0xF0, 0xDA);

    /// <summary>Projekt, dessen Texte gerade in den Reitern stehen.</summary>
    private Guid? _loadedProjectId;

    public AnalysisView(AppServices services, ProjectSelection selection)
    {
        _services = services;
        _selection = selection;
        InitializeComponent();

        _search = new EditorSearch(services, () => SelectedProject?.Id, message => InfoText.Text = message);

        BuildTabs();
        LoadProjects();

        // Ein Projektwechsel in einem anderen Reiter zieht die Analyse sofort mit.
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

    private ProjectOption? SelectedProject => ProjectBox.SelectedItem as ProjectOption;

    /// <summary>Legt je Haupttitel einen Reiter und darin je Unterpunkt einen Reiter an.</summary>
    private void BuildTabs()
    {
        foreach (var chapter in ProjectAnalysisCatalog.All)
        {
            if (chapter.TableKey is not null)
            {
                // „1. Anlagedaten“: je Aufzug eine Zeile; ohne gespeicherte Angaben entstehen
                // die Zeilen aus der Zustandsanalyse. Das Projekt wählt die Auswahl oben.
                _stateTable = new ProjectTableView(_services, ProjectTableCatalog.Anlagedaten,
                    _selection, showProjectSelector: false, draftRows: CreateAnlagedatenRows);
                AddChapterTab(chapter.Heading, _stateTable, () => _stateTable?.HasRows == true);
                continue;
            }

            var sections = new TabControl();
            var sectionTabs = new List<TabStatus>();
            foreach (var section in chapter.Sections)
            {
                var editor = new HtmlEditor();
                editor.EnableSnippetAutocomplete(_search.SearchSnippets);
                editor.EnableImageAutocomplete(_search.SearchImages);
                editor.EnableTableAutocomplete(_search.SearchTableValues);

                var draft = new SectionDraft(section, editor);
                _sections.Add(draft);

                var tab = new TabItem { Content = editor };
                var status = new TabStatus(tab, section.Heading, () => draft.Filled);
                editor.ContentChanged += (_, _) =>
                {
                    draft.Filled = editor.HasText;
                    UpdateTabColors();
                };

                sectionTabs.Add(status);
                _tabStatuses.Add(status);
                sections.Items.Add(tab);
            }

            sections.SelectedIndex = sections.Items.Count > 0 ? 0 : -1;
            AddChapterTab(chapter.Heading, sections, () => sectionTabs.Any(tab => tab.IsFilled()));
        }

        // „4. Fotos“: je Aufzug aus der Zustandsanalyse ein Unter-Reiter mit Bildern.
        AddChapterTab(ProjectAnalysisPhotoCatalog.Heading, _photoTabs,
            () => _photoTabStatuses.Any(tab => tab.IsFilled()));

        // Der Inhalt der Anlagedaten ändert sich erst beim Speichern; die Farbe wird
        // deshalb beim Wechsel auf einen anderen Haupttitel neu bestimmt.
        ChapterTabs.SelectionChanged += (_, _) => UpdateTabColors();

        ChapterTabs.SelectedIndex = ChapterTabs.Items.Count > 0 ? 0 : -1;
        UpdateTabColors();
    }

    /// <summary>Hängt einen Haupttitel mit seiner Farbe an.</summary>
    private void AddChapterTab(string header, object content, Func<bool> isFilled)
    {
        var tab = new TabItem { Content = content };
        _tabStatuses.Add(new TabStatus(tab, header, isFilled));
        ChapterTabs.Items.Add(tab);
    }

    private void LoadProjects(bool keepSelection = false)
    {
        var previousId = keepSelection ? SelectedProject?.Id : _selection.ProjectId;

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

            if (projects.Length == 0)
            {
                ClearEditors();
                InfoText.Text = "Noch keine Projekte vorhanden.";
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    /// <summary>Lädt die gespeicherten Texte des gewählten Projekts in die Reiter.</summary>
    private void LoadTexts()
    {
        if (SelectedProject is not { } project)
        {
            ClearEditors();
            InfoText.Text = "Bitte ein Projekt wählen.";
            return;
        }

        try
        {
            var texts = _services.Analysis.Get(project.Id)
                .ToDictionary(text => text.SectionKey, text => text.Html, StringComparer.OrdinalIgnoreCase);

            foreach (var draft in _sections)
            {
                draft.Editor.SetHtml(texts.TryGetValue(draft.Section.Key, out var html) ? html : string.Empty);
                draft.Filled = draft.Editor.HasText;
            }

            _loadedProjectId = project.Id;
            BuildPhotoTabs(project.Id);
            UpdateTabColors();
            InfoText.Text = texts.Count == 1
                ? "1 Analysepunkt erfasst."
                : $"{texts.Count} von {_sections.Count} Analysepunkten erfasst.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    /// <summary>
    /// Legt die Foto-Reiter an: ein Unterpunkt je Aufzug aus der Zustandsanalyse. Ohne
    /// erfasste Aufzüge bleibt ein Reiter „4.1 Aufzug“, damit Fotos nicht blockiert sind.
    /// </summary>
    private void BuildPhotoTabs(Guid projectId)
    {
        _photoTabs.Items.Clear();
        _tabStatuses.RemoveAll(status => status.IsPhoto);
        _photoTabStatuses.Clear();

        IReadOnlyList<ProjectAnalysisLift> lifts;
        try
        {
            lifts = _services.Analysis.GetLifts(projectId);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
            return;
        }

        if (lifts.Count == 0)
        {
            lifts = [new ProjectAnalysisLift(string.Empty, string.Empty)];
        }

        for (var index = 0; index < lifts.Count; index++)
        {
            var panel = new LiftPhotosPanel(_services, projectId, lifts[index].Key);
            panel.Load();
            panel.PhotosChanged += (_, _) => UpdateTabColors();

            var tab = new TabItem { Content = panel };
            var status = new TabStatus(
                tab,
                ProjectAnalysisPhotoCatalog.LiftHeading(index, lifts[index].Name, lifts[index].Location),
                () => panel.PhotoCount > 0,
                isPhoto: true);

            _photoTabStatuses.Add(status);
            _tabStatuses.Add(status);
            _photoTabs.Items.Add(tab);
        }

        _photoTabs.SelectedIndex = 0;
        UpdateTabColors();
    }

    /// <summary>
    /// Zeilen für „1. Anlagedaten“: je Aufzug der Zustandsanalyse eine Zeile mit deren
    /// Anlagebezeichnung. Sind mehrere Standorte erfasst, steht der Standort dabei.
    /// Gespeichert wird erst auf Knopfdruck.
    /// </summary>
    private IReadOnlyList<ProjectTableRowInput> CreateAnlagedatenRows(Guid projectId)
    {
        IReadOnlyList<ProjectTableRow> lifts;
        try
        {
            lifts = _services.ProjectTables.Get(projectId, ProjectTableCatalog.ZustandsanalyseKey);
        }
        catch (LiftUpException)
        {
            return [];
        }

        var locations = lifts
            .Select(row => Value(row, "Location"))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var withLocation = locations.Length > 1;

        var rows = new List<ProjectTableRowInput>(lifts.Count);
        foreach (var lift in lifts)
        {
            var name = Value(lift, "AssetName");
            var location = Value(lift, "Location");
            var label = withLocation && location.Length > 0 ? $"{name} ({location})" : name;

            rows.Add(new ProjectTableRowInput(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["AssetName"] = label }));
        }

        return rows;
    }

    private static string Value(ProjectTableRow row, string key) =>
        row.Values.TryGetValue(key, out var value) ? (value ?? string.Empty).Trim() : string.Empty;

    private void ClearEditors()
    {
        foreach (var draft in _sections)
        {
            draft.Editor.SetHtml(string.Empty);
            draft.Filled = false;
        }

        UpdateTabColors();
    }

    /// <summary>Ein Reiter mit Beschriftung und der Frage, ob etwas erfasst ist.</summary>
    private sealed class TabStatus
    {
        private readonly Func<bool> _isFilled;
        private bool _painted;

        public TabStatus(TabItem tab, string header, Func<bool> isFilled, bool isPhoto = false)
        {
            Tab = tab;
            Header = header;
            IsPhoto = isPhoto;
            _isFilled = isFilled;
        }

        public TabItem Tab { get; }

        public string Header { get; }

        public bool IsPhoto { get; }

        public bool Filled { get; private set; }

        /// <summary>Der Inhalt, ohne den gemerkten Stand zu verändern.</summary>
        public bool IsFilled() => _isFilled();

        /// <summary>Prüft den Inhalt neu und meldet, ob die Farbe nachgezogen werden muss.</summary>
        public bool Refresh()
        {
            var filled = _isFilled();
            if (_painted && filled == Filled)
            {
                return false;
            }

            Filled = filled;
            _painted = true;
            return true;
        }
    }

    /// <summary>
    /// Färbt die Reiter nach ihrem Inhalt: hellrot, solange nichts erfasst ist, hellgrün,
    /// sobald etwas da ist – so sieht man auf einen Blick, was noch fehlt.
    /// </summary>
    private void UpdateTabColors()
    {
        // Zuerst alle Zustände bestimmen: ein Haupttitel hängt an seinen Unter-Reitern.
        var changed = _tabStatuses.Select(status => status.Refresh()).ToArray();

        for (var index = 0; index < _tabStatuses.Count; index++)
        {
            if (changed[index])
            {
                SetTabHeader(_tabStatuses[index].Tab, _tabStatuses[index].Header, _tabStatuses[index].Filled);
            }
        }
    }

    private static void SetTabHeader(TabItem tab, string header, bool filled)
    {
        tab.Header = new Border
        {
            Background = filled ? DoneBrush : MissingBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 1, 7, 1),
            Child = new TextBlock
            {
                Text = header,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        // Nur die Projektliste neu zuweisen darf die Eingaben nicht verwerfen.
        if (SelectedProject?.Id == _loadedProjectId)
        {
            return;
        }

        LoadTexts();

        // Die Auswahl gilt für alle Reiter (Zustandsanalyse, Investitionsplanung, Analyse, PDF).
        if (SelectedProject is { } project)
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

    private void OnReloadClick(object sender, RoutedEventArgs e) => LoadTexts();

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is not { } project)
        {
            InfoText.Text = "Bitte ein Projekt wählen.";
            return;
        }

        try
        {
            var inputs = new List<ProjectAnalysisTextInput>(_sections.Count);
            foreach (var draft in _sections)
            {
                inputs.Add(new ProjectAnalysisTextInput(draft.Section.Key, await draft.Editor.GetHtmlAsync()));
            }

            var saved = _services.Analysis.Save(project.Id, inputs);
            InfoText.Text = saved.Count == 1
                ? "Analyse gespeichert: 1 Analysepunkt."
                : $"Analyse gespeichert: {saved.Count} von {_sections.Count} Analysepunkten.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            InfoText.Text = "Speichern fehlgeschlagen: " + exception.Message;
        }
    }
}
