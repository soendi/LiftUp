using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiftUp.App.Controls;
using LiftUp.App.Documents;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>
/// Erfasst die Analyse je Projekt: ein Haupttitel („2. Analyse“) mit einem Unter-Reiter
/// per Punkt (2.1 Aggregat, 2.2 Hubzylinder / Tragmittel …). Die Texte hängen am Projekt,
/// damit sie in mehreren Dokumenten wiederverwendet werden.
/// </summary>
public partial class AnalysisView : UserControl, ITabFillSource
{
    private sealed record ProjectOption(Guid Id, string DisplayText);

    /// <summary>Ein Analysepunkt „2.1 … 2.n“ mit Untertitel und Text.</summary>
    private sealed class SectionDraft
    {
        public SectionDraft(string key, HtmlEditor editor, TextBox? titleBox = null)
        {
            Key = key;
            Editor = editor;
            TitleBox = titleBox;
        }

        /// <summary>Schlüssel des gespeicherten Textes; bleibt beim Umbenennen gleich.</summary>
        public string Key { get; }

        public HtmlEditor Editor { get; }

        /// <summary>Feld für den Untertitel; beim Fazit gibt es keins.</summary>
        public TextBox? TitleBox { get; }

        /// <summary>Untertitel, wie er im Reiter nach der Nummer steht.</summary>
        public string Title => (TitleBox?.Text ?? string.Empty).Trim();

        /// <summary>Ob im Reiter etwas steht – bestimmt seine Farbe.</summary>
        public bool Filled { get; set; }

        /// <summary>Reiter samt Farbe; entsteht beim Aufbau der Unter-Reiter.</summary>
        public TabStatus? Status { get; set; }
    }

    private readonly AppServices _services;
    private readonly ProjectSelection _selection;

    /// <summary>Die Analysepunkte „2.1 … 2.n“ – je Punkt ein Reiter unter „2. Analyse“.</summary>
    private readonly List<SectionDraft> _sections = [];

    /// <summary>Der eine Punkt unter „3. Fazit / Empfehlung“.</summary>
    private SectionDraft? _fazit;

    /// <summary>Reiter der Analysepunkte; er wird bei jedem Laden neu aufgebaut.</summary>
    private TabControl? _analysisTabs;

    /// <summary>Beginn und Punkt eines begonnenen Ziehvorgangs in der Reiterleiste.</summary>
    private Point _dragStart;
    private SectionDraft? _dragDraft;

    /// <summary>Füllstand der Analysepunkte für die Farbe von „2. Analyse“.</summary>
    private readonly List<TabStatus> _analysisStatuses = [];

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

    /// <summary>Die Foto-Reiter samt Aufzug und Fotozahl – Grundlage der Nummerierung.</summary>
    private readonly List<(TabStatus Status, Func<int> PhotoCount, ProjectAnalysisLift Lift)> _photoInfos = [];

    /// <summary>Aufzüge, für die gerade ein Foto-Reiter steht.</summary>
    private IReadOnlyList<string> _photoLiftKeys = [];

    /// <summary>Die Haupttitel – Grundlage für die Farbe des Reiters „Analyse“ im Fenster.</summary>
    private readonly List<TabStatus> _chapterStatuses = [];

    /// <summary>Zuletzt gemeldeter Füllstand des ganzen Reiters; null vor dem ersten Lauf.</summary>
    private TabFill? _lastFill;

    /// <summary>Ob seit dem Laden etwas geändert wurde – nur dann speichert der Reiterwechsel.</summary>
    private bool _textsDirty;

    /// <summary>Verhindert, dass sich zwei Speichervorgänge überholen.</summary>
    private bool _savingTexts;

    /// <summary>
    /// Füllstand des Reiters „Analyse“ für die Farbe im Fenster: rot, wenn alle Haupttitel rot
    /// sind, grün, wenn alle grün sind – sonst gelb, auch wenn jeder Titel nur halb erfasst ist.
    /// </summary>
    public TabFill Fill
    {
        get
        {
            if (_chapterStatuses.Count == 0)
            {
                return TabFill.None;
            }

            var statuses = _chapterStatuses.Select(status => status.IsFilled()).ToArray();
            if (statuses.All(status => status == TabFill.Full))
            {
                return TabFill.Full;
            }

            return statuses.All(status => status == TabFill.None) ? TabFill.None : TabFill.Partial;
        }
    }

    /// <summary>Meldet, wenn sich der Füllstand ändert – für die Farbe des Haupt-Reiters.</summary>
    public event EventHandler? FillChanged;

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

        // Ein neu angelegtes Projekt steht sofort in der Auswahl, auch wenn der Reiter zu ist.
        _selection.ProjectsChanged += (_, _) => LoadProjects();

        // Neu angelegte Projekte stehen sofort zur Auswahl, sobald der Reiter geöffnet wird.
        // Beim Verlassen des Reiters werden offene Texte gespeichert – ohne Knopfdruck.
        IsVisibleChanged += async (_, args) =>
        {
            if (args.NewValue is true)
            {
                LoadProjects(keepSelection: true);
                RefreshTables();
            }
            else
            {
                await SaveIfDirtyAsync();
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
                // Die Tabelle meldet jede Änderung – auch nach dem Speichern. Ohne das bliebe die
                // Farbe beim alten Stand, bis ein anderer Haupttitel gewählt wird.
                _stateTable.FillChanged += (_, _) => UpdateTabColors();
                AddChapterTab(chapter.Heading, _stateTable, () => _stateTable?.Fill ?? TabFill.None);
                continue;
            }

            // „2. Analyse“: Die Punkte 2.1 … 2.n stehen nicht fest – sie entstehen aus den
            // erfassten Texten und lassen sich im Reiter hinzufügen und löschen.
            if (chapter.Key == ProjectAnalysisCatalog.AnalyseKey)
            {
                _analysisTabs = new TabControl();
                AddChapterTab(chapter.Heading, BuildAnalysisPanel(_analysisTabs),
                    () => Combine(_analysisStatuses));
                continue;
            }

            // Ein Haupttitel mit nur einem Punkt bekommt keinen Unter-Reiter: Der Editor steht
            // direkt darunter („3. Fazit / Empfehlung“ ohne „3.1 …“).
            if (chapter.Sections.Count == 1)
            {
                var single = CreateDraft(chapter.Sections[0].Key);
                _fazit = single;
                AddChapterTab(chapter.Heading, single.Editor,
                    () => single.Filled ? TabFill.Full : TabFill.None);
                continue;
            }
        }

        // „4. Fotos“: je Aufzug aus der Zustandsanalyse ein Unter-Reiter mit Bildern.
        AddChapterTab(ProjectAnalysisPhotoCatalog.Heading, _photoTabs, () => Combine(_photoTabStatuses));

        // Der Inhalt der Anlagedaten ändert sich erst beim Speichern; die Farbe wird
        // deshalb beim Wechsel auf einen anderen Haupttitel neu bestimmt.
        ChapterTabs.SelectionChanged += (_, _) => UpdateTabColors();

        ChapterTabs.SelectedIndex = ChapterTabs.Items.Count > 0 ? 0 : -1;
        UpdateTabColors();
    }

    /// <summary>Hängt einen Haupttitel mit seiner Farbe an.</summary>
    private void AddChapterTab(string header, object content, Func<TabFill> fill)
    {
        var tab = new TabItem { Content = content };
        var status = new TabStatus(tab, header, fill);
        _tabStatuses.Add(status);
        _chapterStatuses.Add(status);
        ChapterTabs.Items.Add(tab);
    }

    /// <summary>Editor eines Analysepunkts samt „/“-, „//“- und „&amp;“-Suche und Farbsignal.</summary>
    private HtmlEditor CreateEditor()
    {
        var editor = new HtmlEditor { SnippetServices = _services };
        // Bilder lassen sich hier direkt einsetzen; ohne Projektordner bleibt die Datei, wo sie ist.
        editor.EnableImageInsert(_services, () => SelectedProject?.Id);
        editor.EnableSnippetAutocomplete(_search.SearchSnippets);
        editor.EnableImageAutocomplete(_search.SearchImages);
        editor.EnableTableAutocomplete(_search.SearchTableValues);
        return editor;
    }

    /// <summary>Neuer Punkt mit Editor, optionalem Untertitel-Feld und Farbsignal.</summary>
    private SectionDraft CreateDraft(string key, bool withTitle = false)
    {
        var editor = CreateEditor();
        TextBox? titleBox = null;

        if (withTitle)
        {
            titleBox = new TextBox
            {
                Width = 420,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                ToolTip = "Untertitel des Punkts, etwa „Aggregat“",
            };
            // Der Reiter heißt sofort „2.1 Untertitel“, sobald einer gesetzt ist.
            titleBox.TextChanged += (_, _) =>
            {
                _textsDirty = true;
                NumberSections();
            };
        }

        var draft = new SectionDraft(key, editor, titleBox);

        editor.ContentChanged += (_, _) =>
        {
            _textsDirty = true;
            draft.Filled = editor.HasText;
            UpdateTabColors();
        };

        return draft;
    }

    /// <summary>Reiter „2. Analyse“: Knöpfe für neue und gelöschte Punkte über den Unter-Reitern.</summary>
    private FrameworkElement BuildAnalysisPanel(TabControl tabs)
    {
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var add = new Button { Content = "Punkt hinzufügen" };
        add.Click += (_, _) => AddSection();

        var remove = new Button
        {
            Content = "Punkt löschen",
            Style = (Style)FindResource("LiftUpDangerButton"),
        };
        remove.Click += (_, _) => RemoveSelectedSection();

        toolbar.Children.Add(add);
        toolbar.Children.Add(remove);

        // Reihenfolge ändern: einen Reiter greifen und auf einen anderen fallen lassen.
        tabs.AllowDrop = true;
        tabs.PreviewMouseLeftButtonDown += OnAnalysisTabMouseDown;
        tabs.PreviewMouseMove += OnAnalysisTabMouseMove;
        tabs.DragOver += OnAnalysisTabDragOver;
        tabs.Drop += OnAnalysisTabDrop;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(tabs, 1);
        grid.Children.Add(toolbar);
        grid.Children.Add(tabs);
        return grid;
    }

    /// <summary>
    /// Baut die Unter-Reiter von „2. Analyse“ aus den gespeicherten Texten. Ohne erfasste
    /// Punkte steht genau „2.1“ da – weitere Punkte kommen über „Punkt hinzufügen“.
    /// </summary>
    private void BuildAnalysisTabs(IReadOnlyList<ProjectAnalysisText> texts)
    {
        if (_analysisTabs is null)
        {
            return;
        }

        foreach (var draft in _sections)
        {
            if (draft.Status is not { } status)
            {
                continue;
            }

            _tabStatuses.Remove(status);
            _analysisStatuses.Remove(status);
        }

        _sections.Clear();
        _analysisTabs.Items.Clear();

        var points = texts.Where(text => !ProjectAnalysisCatalog.IsFazit(text.SectionKey)).ToArray();
        if (points.Length == 0)
        {
            AddSectionTab(NewSectionKey(), string.Empty, string.Empty);
        }
        else
        {
            foreach (var point in points)
            {
                AddSectionTab(point.SectionKey, point.Title, point.Html);
            }
        }

        _analysisTabs.SelectedIndex = 0;
        NumberSections();
    }

    /// <summary>Hängt einen Analysepunkte-Reiter mit Untertitel-Feld und Text an.</summary>
    private void AddSectionTab(string key, string title, string html)
    {
        var draft = CreateDraft(key, withTitle: true);
        draft.TitleBox!.Text = title;
        draft.Editor.SetHtml(html);
        draft.Filled = draft.Editor.HasText;

        _sections.Add(draft);

        var tab = new TabItem { Content = SectionContent(draft), Tag = draft };
        var status = new TabStatus(tab, string.Empty, () => draft.Filled ? TabFill.Full : TabFill.None);
        draft.Status = status;

        _tabStatuses.Add(status);
        _analysisStatuses.Add(status);
        _analysisTabs!.Items.Add(tab);
        NumberSections();
    }

    /// <summary>Untertitel-Feld über dem Text des Punkts.</summary>
    private FrameworkElement SectionContent(SectionDraft draft)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
        };
        header.Children.Add(new TextBlock
        {
            Text = "Untertitel",
            Style = (Style)FindResource("LiftUpFieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        header.Children.Add(draft.TitleBox!);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(draft.Editor, 1);
        grid.Children.Add(header);
        grid.Children.Add(draft.Editor);
        return grid;
    }

    /// <summary>
    /// Nummeriert die Reiter nach ihrer Reihenfolge und übernimmt den Untertitel:
    /// aus „2.1“ wird „2.1 Aggregat“, sobald einer gesetzt ist.
    /// </summary>
    private void NumberSections()
    {
        for (var index = 0; index < _sections.Count; index++)
        {
            var draft = _sections[index];
            var number = $"2.{index + 1}";
            draft.Status?.SetHeader(draft.Title.Length == 0 ? number : $"{number} {draft.Title}");
        }

        UpdateTabColors();
    }

    /// <summary>Merkt sich den gedrückten Reiter – Grundlage für das Ziehen.</summary>
    private void OnAnalysisTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabControl tabs)
        {
            return;
        }

        _dragStart = e.GetPosition(tabs);
        var (inHeader, tab) = HitHeader(tabs, _dragStart);
        _dragDraft = inHeader ? tab?.Tag as SectionDraft : null;
    }

    private void OnAnalysisTabMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not TabControl tabs || _dragDraft is not { } draft)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragDraft = null;
            return;
        }

        var position = e.GetPosition(tabs);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragDraft = null;
        DragDrop.DoDragDrop(draft.Status!.Tab, new DataObject(typeof(SectionDraft), draft), DragDropEffects.Move);
    }

    private void OnAnalysisTabDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(SectionDraft)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    /// <summary>Setzt den gezogenen Punkt an die Stelle des Reiters, auf den er fällt.</summary>
    private void OnAnalysisTabDrop(object sender, DragEventArgs e)
    {
        if (sender is not TabControl tabs || e.Data.GetData(typeof(SectionDraft)) is not SectionDraft draft)
        {
            return;
        }

        var (inHeader, target) = HitHeader(tabs, e.GetPosition(tabs));
        if (!inHeader)
        {
            return;
        }

        e.Handled = true;
        MoveSection(draft, target?.Tag as SectionDraft);
    }

    /// <summary>Verschiebt einen Punkt vor den Punkt, auf den er gezogen wurde.</summary>
    private void MoveSection(SectionDraft draft, SectionDraft? target)
    {
        if (_analysisTabs is null)
        {
            return;
        }

        var from = _sections.IndexOf(draft);
        if (from < 0)
        {
            return;
        }

        // Ohne Reiter unter dem Zeiger landet der Punkt am Ende.
        var to = target is null ? _sections.Count - 1 : _sections.IndexOf(target);
        if (to < 0 || to == from)
        {
            return;
        }

        _sections.RemoveAt(from);
        _sections.Insert(to, draft);

        _analysisTabs.Items.RemoveAt(from);
        _analysisTabs.Items.Insert(to, draft.Status!.Tab);
        _analysisTabs.SelectedItem = draft.Status.Tab;
        NumberSections();
    }

    /// <summary>
    /// Treffer in der Reiterleiste: ob der Zeiger auf den Reitern steht und auf welchem.
    /// Der Text darunter darf kein Ziehen auslösen.
    /// </summary>
    private static (bool InHeader, TabItem? Tab) HitHeader(TabControl tabs, Point position)
    {
        var panel = FindHeaderPanel(tabs);
        if (panel is null)
        {
            return (false, null);
        }

        var bounds = panel.TransformToAncestor(tabs).TransformBounds(new Rect(panel.RenderSize));
        if (!bounds.Contains(position))
        {
            return (false, null);
        }

        var hit = tabs.InputHitTest(position) as DependencyObject;
        while (hit is not null and not TabItem)
        {
            hit = VisualTreeHelper.GetParent(hit);
        }

        return (true, hit as TabItem);
    }

    /// <summary>Der Streifen mit den Reitern im Template des TabControl.</summary>
    private static TabPanel? FindHeaderPanel(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TabPanel panel)
            {
                return panel;
            }

            if (FindHeaderPanel(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Schlüssel eines neuen Punkts; er bleibt beim Speichern und Umbenennen gleich.</summary>
    private static string NewSectionKey() => "P" + Guid.NewGuid().ToString("N");

    private void AddSection()
    {
        AddSectionTab(NewSectionKey(), string.Empty, string.Empty);
        if (_analysisTabs is not null)
        {
            _analysisTabs.SelectedIndex = _analysisTabs.Items.Count - 1;
        }

        FocusSectionTitle(_sections[^1]);
    }

    /// <summary>
    /// Setzt den Fokus in das Untertitel-Feld eines Punkts. Der Reiterinhalt entsteht erst
    /// beim nächsten Layoutlauf – ein sofortiges Fokussieren ginge deshalb ins Leere.
    /// </summary>
    private void FocusSectionTitle(SectionDraft draft)
    {
        if (draft.TitleBox is not { } titleBox)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                titleBox.Focus();
                Keyboard.Focus(titleBox);
                titleBox.CaretIndex = titleBox.Text.Length;
            }));
    }

    /// <summary>Entfernt den gewählten Punkt; ohne Punkte bleibt „2.1“ stehen.</summary>
    private void RemoveSelectedSection()
    {
        if (_analysisTabs is null || _sections.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(_analysisTabs.SelectedIndex, 0, _sections.Count - 1);
        var draft = _sections[index];

        if (draft.Status is { } status)
        {
            _tabStatuses.Remove(status);
            _analysisStatuses.Remove(status);
        }

        _analysisTabs.Items.RemoveAt(index);
        _sections.RemoveAt(index);

        if (_sections.Count == 0)
        {
            AddSectionTab(NewSectionKey(), string.Empty, string.Empty);
        }

        _analysisTabs.SelectedIndex = Math.Min(index, _sections.Count - 1);
        NumberSections();
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
            var texts = _services.Analysis.Get(project.Id);
            var byKey = texts.ToDictionary(text => text.SectionKey, StringComparer.OrdinalIgnoreCase);

            if (_fazit is not null)
            {
                _fazit.Editor.SetHtml(byKey.TryGetValue(_fazit.Key, out var fazit) ? fazit.Html : string.Empty);
                _fazit.Filled = _fazit.Editor.HasText;
            }

            BuildAnalysisTabs(texts);

            _loadedProjectId = project.Id;
            BuildPhotoTabs(project.Id);
            UpdateTabColors();

            var filled = _sections.Count(draft => draft.Filled) + (_fazit is { Filled: true } ? 1 : 0);
            var total = _sections.Count + 1;
            InfoText.Text = filled == 1
                ? "1 Analysepunkt erfasst."
                : $"{filled} von {total} Analysepunkten erfasst.";

            // Der geladene Stand ist gespeichert; erst eine Eingabe macht den Reiter wieder offen.
            _textsDirty = false;
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    /// <summary>
    /// Zieht Anlagedaten und Foto-Reiter nach, wenn im Reiter „Zustandsanalyse“ ein Aufzug
    /// erfasst wurde. Die erfassten Texte bleiben stehen; die Anlagedaten werden nur geladen,
    /// solange der Reiter leer ist, damit ungespeicherte Zeilen nicht verloren gehen.
    /// </summary>
    private void RefreshTables()
    {
        if (SelectedProject is not { } project)
        {
            return;
        }

        if (_stateTable is { HasRows: false })
        {
            _stateTable.Reload();
        }

        // Neue oder weggefallene Aufzüge: nur dann entstehen die Foto-Reiter neu, damit
        // bereits hinzugefügte Fotos nicht aus der Ansicht fallen.
        if (!_photoLiftKeys.SequenceEqual(CurrentPhotoLiftKeys(project.Id)))
        {
            BuildPhotoTabs(project.Id);
        }

        UpdateTabColors();
    }

    /// <summary>Aufzüge, die jetzt einen Foto-Reiter hätten; bei Fehlern der bisherige Stand.</summary>
    private IReadOnlyList<string> CurrentPhotoLiftKeys(Guid projectId)
    {
        try
        {
            return _services.Analysis.GetLifts(projectId).Select(lift => lift.Key).ToArray();
        }
        catch (LiftUpException)
        {
            return _photoLiftKeys;
        }
    }

    /// <summary>
    /// Legt die Foto-Reiter an: ein Unterpunkt je Aufzug aus der Zustandsanalyse, sobald
    /// Anlagebezeichnung und Standort erfasst sind. Ohne solche Zeilen bleibt der Haupttitel
    /// ohne Unter-Reiter.
    /// </summary>
    private void BuildPhotoTabs(Guid projectId)
    {
        _photoTabs.Items.Clear();
        _tabStatuses.RemoveAll(status => status.IsPhoto);
        _photoTabStatuses.Clear();
        _photoInfos.Clear();

        IReadOnlyList<ProjectAnalysisLift> lifts;
        try
        {
            lifts = _services.Analysis.GetLifts(projectId);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
            _photoLiftKeys = [];
            return;
        }

        _photoLiftKeys = lifts.Select(lift => lift.Key).ToArray();

        foreach (var lift in lifts)
        {
            var panel = new LiftPhotosPanel(_services, projectId, lift.Key);
            panel.Load();

            var tab = new TabItem { Content = panel };
            var status = new TabStatus(
                tab,
                string.Empty,
                () => panel.PhotoCount > 0 ? TabFill.Full : TabFill.None,
                isPhoto: true);

            // Die Nummer wird erst nach dem Laden verteilt: Nur Aufzüge mit Fotos zählen mit.
            _photoInfos.Add((status, () => panel.PhotoCount, lift));
            panel.PhotosChanged += (_, _) =>
            {
                RenumberPhotoTabs();
                UpdateTabColors();
            };

            _photoTabStatuses.Add(status);
            _tabStatuses.Add(status);
            _photoTabs.Items.Add(tab);
        }

        RenumberPhotoTabs();
        _photoTabs.SelectedIndex = 0;
        UpdateTabColors();
    }

    /// <summary>
    /// Nummeriert die Foto-Reiter nach den Aufzügen mit Fotos: „4.1“, „4.2“ … ohne Lücke.
    /// Ein Aufzug ohne Fotos steht ohne Nummer da; kommt ein Foto dazu oder fällt ein Aufzug
    /// weg, rücken die Nummern nach.
    /// </summary>
    private void RenumberPhotoTabs()
    {
        var number = 0;
        foreach (var (status, photoCount, lift) in _photoInfos)
        {
            status.SetHeader(photoCount() > 0
                ? ProjectAnalysisPhotoCatalog.LiftHeading(number++, lift.Name, lift.Location)
                : ProjectAnalysisPhotoCatalog.LiftHeading(lift.Name, lift.Location));
        }
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

        if (_fazit is not null)
        {
            _fazit.Editor.SetHtml(string.Empty);
            _fazit.Filled = false;
        }

        _textsDirty = false;
        UpdateTabColors();
    }

    /// <summary>Ein Reiter mit Beschriftung und seinem Füllstand für die Farbe.</summary>
    private sealed class TabStatus
    {
        private readonly Func<TabFill> _status;
        private bool _painted;

        public TabStatus(TabItem tab, string header, Func<TabFill> status, bool isPhoto = false)
        {
            Tab = tab;
            Header = header;
            IsPhoto = isPhoto;
            _status = status;
        }

        public TabItem Tab { get; }

        public string Header { get; private set; }

        /// <summary>Neue Beschriftung, etwa nach dem Umbennennen der Foto-Reiter.</summary>
        public void SetHeader(string header)
        {
            if (string.Equals(Header, header, StringComparison.Ordinal))
            {
                return;
            }

            Header = header;
            _painted = false;
        }

        public bool IsPhoto { get; }

        public TabFill Filled { get; private set; }

        /// <summary>Der Inhalt, ohne den gemerkten Stand zu verändern.</summary>
        public TabFill IsFilled() => _status();

        /// <summary>Prüft den Inhalt neu und meldet, ob die Farbe nachgezogen werden muss.</summary>
        public bool Refresh()
        {
            var status = _status();
            if (_painted && status == Filled)
            {
                return false;
            }

            Filled = status;
            _painted = true;
            return true;
        }
    }

    /// <summary>
    /// Füllstand eines Haupttitels aus seinen Unter-Reitern: rot, wenn keiner erfasst ist,
    /// gelb, wenn einige fehlen, grün, wenn alle erfasst sind.
    /// </summary>
    private static TabFill Combine(IReadOnlyList<TabStatus> tabs)
    {
        if (tabs.Count == 0)
        {
            return TabFill.None;
        }

        var full = tabs.Count(tab => tab.IsFilled() == TabFill.Full);
        return full == 0
            ? TabFill.None
            : full == tabs.Count ? TabFill.Full : TabFill.Partial;
    }

    /// <summary>
    /// Färbt die Reiter nach ihrem Inhalt: hellrot, solange nichts erfasst ist, hellgelb,
    /// solange etwas fehlt, hellgrün, sobald alles erfasst ist – so sieht man auf einen Blick,
    /// was noch fehlt.
    /// </summary>
    private void UpdateTabColors()
    {
        // Zuerst alle Zustände bestimmen: ein Haupttitel hängt an seinen Unter-Reitern.
        var changed = _tabStatuses.Select(status => status.Refresh()).ToArray();

        for (var index = 0; index < _tabStatuses.Count; index++)
        {
            if (changed[index])
            {
                TabHeaderStyle.Apply(_tabStatuses[index].Tab, _tabStatuses[index].Header, _tabStatuses[index].Filled);
            }
        }

        // Der Haupt-Reiter im Fenster folgt dem Füllstand der ganzen Analyse.
        if (_lastFill != Fill)
        {
            _lastFill = Fill;
            FillChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        // Nur die Projektliste neu zuweisen darf die Eingaben nicht verwerfen.
        if (SelectedProject?.Id == _loadedProjectId)
        {
            return;
        }

        // Erst die Texte des bisherigen Projekts sichern, dann die neuen laden.
        await SaveIfDirtyAsync();

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

        await SaveTextsAsync(project.Id, automatic: false);
    }

    /// <summary>
    /// Ob Texte offen sind, die vor dem Schließen des Fensters noch gespeichert werden müssen.
    /// </summary>
    public bool HasUnsavedTexts => _textsDirty && !_savingTexts;

    /// <summary>
    /// Speichert offene Texte ohne Knopfdruck; ohne Änderung passiert nichts. Aufgerufen wird
    /// das beim Verlassen des Reiters, beim Projektwechsel und vor dem Schließen des Fensters.
    /// </summary>
    public async Task<bool> SaveIfDirtyAsync()
    {
        if (!_textsDirty || _savingTexts || _loadedProjectId is not { } projectId)
        {
            return false;
        }

        return await SaveTextsAsync(projectId, automatic: true);
    }

    /// <summary>
    /// Schreibt die Texte des Projekts: alle Analysepunkte samt Untertitel und das Fazit.
    /// <paramref name="automatic"/> ändert nur die Rückmeldung in der Fußzeile.
    /// </summary>
    private async Task<bool> SaveTextsAsync(Guid projectId, bool automatic)
    {
        if (_savingTexts)
        {
            return false;
        }

        _savingTexts = true;
        try
        {
            var inputs = new List<ProjectAnalysisTextInput>(_sections.Count + 1);
            foreach (var draft in _sections)
            {
                inputs.Add(new ProjectAnalysisTextInput(draft.Key, draft.Title, await draft.Editor.GetHtmlAsync()));
            }

            if (_fazit is not null)
            {
                inputs.Add(new ProjectAnalysisTextInput(_fazit.Key, string.Empty, await _fazit.Editor.GetHtmlAsync()));
            }

            var saved = _services.Analysis.Save(projectId, inputs);
            _textsDirty = false;

            var prefix = automatic ? "Analyse automatisch gespeichert" : "Analyse gespeichert";
            InfoText.Text = saved.Count == 1
                ? $"{prefix}: 1 Analysepunkt."
                : $"{prefix}: {saved.Count} Analysepunkte.";
            return true;
        }
        catch (LiftUpException exception)
        {
            // Eingaben bleiben stehen; der nächste Reiterwechsel versucht es erneut.
            InfoText.Text = exception.Message;
            return false;
        }
        catch (InvalidOperationException exception)
        {
            InfoText.Text = "Speichern fehlgeschlagen: " + exception.Message;
            return false;
        }
        finally
        {
            _savingTexts = false;
        }
    }
}
