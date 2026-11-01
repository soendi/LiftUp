using System.Windows;
using System.Windows.Controls;
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

    private sealed record SectionDraft(ProjectAnalysisSection Section, HtmlEditor Editor);

    private readonly AppServices _services;
    private readonly ProjectSelection _selection;
    private readonly List<SectionDraft> _sections = [];

    /// <summary>Suche für „/“ (Textbausteine) und „&amp;“ / „&amp;&amp;“ (Bilder) in den Editoren.</summary>
    private readonly EditorSearch _search;

    /// <summary>Tabelle unter „1. Anlagedaten“ – dieselbe Datenaufnahme wie unter „Zustandsanalyse“.</summary>
    private ProjectTableView? _stateTable;

    /// <summary>Unter-Reiter je Aufzug unter „4. Fotos“.</summary>
    private readonly TabControl _photoTabs = new();

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
                // „1. Anlagedaten“: die Datenaufnahme der Aufzüge als Tabelle. Das Projekt
                // wählt die Auswahl oben; die Tabelle zieht mit.
                _stateTable = new ProjectTableView(_services, ProjectTableCatalog.Zustandsanalyse,
                    _selection, showProjectSelector: false);
                ChapterTabs.Items.Add(new TabItem { Header = chapter.Heading, Content = _stateTable });
                continue;
            }

            var sections = new TabControl();
            foreach (var section in chapter.Sections)
            {
                var editor = new HtmlEditor();
                editor.EnableSnippetAutocomplete(_search.SearchSnippets);
                editor.EnableImageAutocomplete(_search.SearchImages);
                editor.EnableTableAutocomplete(_search.SearchTableValues);
                var tab = new TabItem { Header = section.Heading, Content = editor };
                sections.Items.Add(tab);
                _sections.Add(new SectionDraft(section, editor));
            }

            sections.SelectedIndex = sections.Items.Count > 0 ? 0 : -1;
            ChapterTabs.Items.Add(new TabItem { Header = chapter.Heading, Content = sections });
        }

        // „4. Fotos“: je Aufzug aus der Zustandsanalyse ein Unter-Reiter mit Bildern.
        ChapterTabs.Items.Add(new TabItem
        {
            Header = ProjectAnalysisPhotoCatalog.Heading,
            Content = _photoTabs,
        });

        ChapterTabs.SelectedIndex = ChapterTabs.Items.Count > 0 ? 0 : -1;
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
            }

            _loadedProjectId = project.Id;
            BuildPhotoTabs(project.Id);
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

            _photoTabs.Items.Add(new TabItem
            {
                Header = ProjectAnalysisPhotoCatalog.LiftHeading(index, lifts[index].Name, lifts[index].Location),
                Content = panel,
            });
        }

        _photoTabs.SelectedIndex = 0;
    }

    private void ClearEditors()
    {
        foreach (var draft in _sections)
        {
            draft.Editor.SetHtml(string.Empty);
        }
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
