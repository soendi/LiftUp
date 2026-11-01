using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>
/// Eine projektbezogene Tabelle (Zustandsanalyse oder Investitionsplanung). Die Spalten
/// stammen aus <see cref="ProjectTableCatalog"/>; in Textfeldern liefert „/“ die Textbausteine.
/// </summary>
public partial class ProjectTableView : UserControl
{
    private sealed record ProjectOption(Guid Id, string DisplayText);

    private sealed record SnippetOption(Guid Id, string Name, string Category)
    {
        public string DisplayText => string.IsNullOrWhiteSpace(Category) ? Name : $"{Name} · {Category}";
    }

    private readonly AppServices _services;
    private readonly ProjectTableDefinition _definition;
    private readonly ProjectSelection _selection;
    private readonly ObservableCollection<ProjectTableRowModel> _rows = [];
    private readonly List<SnippetOption> _snippets = [];
    private readonly List<SnippetOption> _matches = [];

    private TextBox? _editingBox;
    private int _slashIndex = -1;
    private int _selectedMatch = -1;

    /// <param name="showProjectSelector">
    /// <c>false</c>, wenn die Tabelle in einem Reiter mit eigener Projektauswahl steht
    /// (etwa „1. Anlagedaten“ der Analyse); das Projekt kommt dann über <see cref="SelectProject"/>.
    /// </param>
    public ProjectTableView(
        AppServices services,
        ProjectTableDefinition definition,
        ProjectSelection selection,
        bool showProjectSelector = true)
    {
        _services = services;
        _definition = definition;
        _selection = selection;

        InitializeComponent();

        if (!showProjectSelector)
        {
            ProjectBar.Visibility = Visibility.Collapsed;
        }

        BuildColumns();
        LoadSnippets();
        LoadProjects();

        // Ein Projektwechsel in einem anderen Reiter zieht die Tabelle sofort mit.
        _selection.Changed += (_, _) => ShowSelectedProject();
        ShowSelectedProject();

        RowsGrid.ItemsSource = _rows;
        RowsGrid.PreparingCellForEdit += OnPreparingCellForEdit;
        RowsGrid.CellEditEnding += OnCellEditEnding;
        Unloaded += (_, _) => HideSnippets();
    }

    /// <summary>
    /// Legt je Spalte der Definition eine Tabellenspalte an. Spalten mit fester Auswahl
    /// (Bewertungen, ja/nein, Aufzugsart) bekommen eine Auswahlliste statt eines Textfelds.
    /// </summary>
    private void BuildColumns()
    {
        foreach (var column in _definition.Columns)
        {
            var binding = new Binding($"[{column.Key}]")
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            };

            var width = column.Width > 0
                ? new DataGridLength(column.Width)
                : new DataGridLength(1, DataGridLengthUnitType.Star);

            var header = new TextBlock
            {
                Text = column.Label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = column.Options.Count > 0
                    ? $"{column.Label} ({string.Join(", ", column.Options)})"
                    : column.Label,
            };

            if (column.Options.Count > 0)
            {
                RowsGrid.Columns.Add(new DataGridComboBoxColumn
                {
                    Header = header,
                    SelectedItemBinding = binding,
                    ItemsSource = column.Options.ToArray(),
                    Width = width,
                });
                continue;
            }

            RowsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = binding,
                Width = width,
            });
        }
    }

    private void LoadSnippets()
    {
        try
        {
            _snippets.AddRange(_services.Snippets.GetAll()
                .Select(snippet => new SnippetOption(snippet.Id, snippet.Name, snippet.Category ?? string.Empty)));
        }
        catch (LiftUpException)
        {
            // Ohne Recht auf Textbausteine bleibt die „/“-Suche einfach leer.
        }
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

    private void LoadRows()
    {
        _rows.Clear();

        if (ProjectBox.SelectedItem is not ProjectOption project)
        {
            InfoText.Text = "Bitte ein Projekt wählen.";
            return;
        }

        try
        {
            foreach (var row in _services.ProjectTables.Get(project.Id, _definition.Key))
            {
                _rows.Add(new ProjectTableRowModel(row));
            }

            InfoText.Text = _rows.Count == 1
                ? $"{_definition.Title}: 1 Zeile"
                : $"{_definition.Title}: {_rows.Count} Zeilen";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        HideSnippets();
        LoadRows();

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

    private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadRows();

    private void OnAddRowClick(object sender, RoutedEventArgs e)
    {
        var model = new ProjectTableRowModel();
        _rows.Add(model);
        RowsGrid.SelectedItem = model;
        RowsGrid.ScrollIntoView(model);
        InfoText.Text = "Zeile angelegt – bitte ausfüllen und speichern.";
    }

    private void OnRemoveRowClick(object sender, RoutedEventArgs e)
    {
        if (RowsGrid.SelectedItem is not ProjectTableRowModel model)
        {
            InfoText.Text = "Bitte zuerst eine Zeile auswählen.";
            return;
        }

        _rows.Remove(model);
        InfoText.Text = "Zeile entfernt – bitte speichern.";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ProjectBox.SelectedItem is not ProjectOption project)
        {
            InfoText.Text = "Bitte ein Projekt wählen.";
            return;
        }

        // Eine offene Zelle muss vor dem Speichern übernommen werden.
        RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        HideSnippets();

        try
        {
            var inputs = _rows
                .Select(row => new ProjectTableRowInput(row.ToValues()))
                .ToArray();

            var saved = _services.ProjectTables.Save(project.Id, _definition.Key, inputs);

            _rows.Clear();
            foreach (var row in saved)
            {
                _rows.Add(new ProjectTableRowModel(row));
            }

            InfoText.Text = saved.Count == 1
                ? "Gespeichert: 1 Zeile."
                : $"Gespeichert: {saved.Count} Zeilen.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is not TextBox box)
        {
            return;
        }

        DetachEditingBox();
        _editingBox = box;
        box.TextChanged += OnCellTextChanged;
        box.PreviewKeyDown += OnCellPreviewKeyDown;
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        DetachEditingBox();
        HideSnippets();
    }

    private void DetachEditingBox()
    {
        if (_editingBox is not null)
        {
            _editingBox.TextChanged -= OnCellTextChanged;
            _editingBox.PreviewKeyDown -= OnCellPreviewKeyDown;
            _editingBox = null;
        }
    }

    /// <summary>
    /// Zeigt die Textbausteine, sobald hinter „/“ getippt wird – „/“ allein zeigt alle.
    /// Ein Schrägstrich mitten im Wort (etwa „Wohn-/Geschäftshaus“) löst nichts aus.
    /// </summary>
    private void OnCellTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || _snippets.Count == 0)
        {
            return;
        }

        var caret = Math.Min(box.CaretIndex, box.Text.Length);
        var before = box.Text[..caret];
        var slash = before.LastIndexOf('/');

        if (slash < 0 || (slash > 0 && !char.IsWhiteSpace(before[slash - 1])))
        {
            HideSnippets();
            return;
        }

        var token = before[(slash + 1)..];
        if (token.Contains(' ') || token.Length > 40)
        {
            HideSnippets();
            return;
        }

        ShowSnippets(box, slash, token);
    }

    private void OnCellPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SnippetPopup.IsOpen || _matches.Count == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                _selectedMatch = Math.Min(_selectedMatch + 1, _matches.Count - 1);
                BuildSnippetList();
                e.Handled = true;
                break;
            case Key.Up:
                _selectedMatch = Math.Max(_selectedMatch - 1, 0);
                BuildSnippetList();
                e.Handled = true;
                break;
            case Key.Enter when _selectedMatch >= 0:
                PickSnippet(_matches[_selectedMatch]);
                e.Handled = true;
                break;
            case Key.Escape:
                HideSnippets();
                e.Handled = true;
                break;
        }
    }

    private void ShowSnippets(TextBox box, int slashIndex, string token)
    {
        _matches.Clear();
        _matches.AddRange(_snippets.Where(snippet =>
            token.Length == 0
            || snippet.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
            || snippet.Category.Contains(token, StringComparison.OrdinalIgnoreCase)));

        _slashIndex = slashIndex;
        _selectedMatch = 0;

        if (_matches.Count == 0)
        {
            HideSnippets();
            return;
        }

        BuildSnippetList();
        PositionPopup(box);
        SnippetPopup.IsOpen = true;
    }

    private void BuildSnippetList()
    {
        SnippetList.Children.Clear();

        for (var index = 0; index < _matches.Count; index++)
        {
            var match = _matches[index];
            var row = new Button
            {
                Content = new TextBlock
                {
                    Text = match.DisplayText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(0),
                Background = index == _selectedMatch
                    ? new SolidColorBrush(Color.FromRgb(0xE3, 0xEE, 0xFA))
                    : Brushes.Transparent,
                ToolTip = match.DisplayText,
            };

            var choice = match;
            row.Click += (_, _) => PickSnippet(choice);
            SnippetList.Children.Add(row);
        }
    }

    private void PositionPopup(TextBox box)
    {
        var caret = Math.Min(box.CaretIndex, box.Text.Length);
        var rect = box.GetRectFromCharacterIndex(caret, trailingEdge: false);
        var point = box.TranslatePoint(new Point(rect.Left, rect.Bottom), RowsGrid);

        SnippetPopup.PlacementTarget = RowsGrid;
        SnippetPopup.Placement = PlacementMode.Relative;
        SnippetPopup.HorizontalOffset = Math.Max(0, point.X);
        SnippetPopup.VerticalOffset = Math.Max(0, point.Y);
    }

    /// <summary>
    /// Setzt den Textbaustein an die Stelle des „/“-Suchtextes. In einer Tabellenzelle gibt
    /// es keine Formatierung, deshalb wird der HTML-Inhalt als reiner Text übernommen.
    /// </summary>
    private void PickSnippet(SnippetOption option)
    {
        var box = _editingBox;
        if (box is null || _slashIndex < 0)
        {
            HideSnippets();
            return;
        }

        string html;
        try
        {
            html = _services.Snippets.Get(option.Id).Html;
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
            HideSnippets();
            return;
        }

        var text = ToPlainText(html);
        var caret = Math.Min(box.CaretIndex, box.Text.Length);
        var start = Math.Min(_slashIndex, box.Text.Length);

        box.Text = box.Text[..start] + text + box.Text[caret..];
        box.CaretIndex = start + text.Length;
        HideSnippets();
    }

    private void HideSnippets()
    {
        SnippetPopup.IsOpen = false;
        SnippetList.Children.Clear();
        _matches.Clear();
        _slashIndex = -1;
        _selectedMatch = -1;
    }

    private static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = Regex.Replace(html, "<br\\s*/?>|</p>|</div>|</li>|</h[1-6]>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, "\\s+", " ").Trim();
    }
}

/// <summary>Eine Zeile der Tabelle; die Werte hängen an den Schlüsseln der Spalten.</summary>
public sealed class ProjectTableRowModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, string> _values;

    public ProjectTableRowModel(ProjectTableRow? row = null)
    {
        _values = row is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(row.Values, StringComparer.OrdinalIgnoreCase);
        SourceId = row?.Id;
    }

    public Guid? SourceId { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key]
    {
        get => _values.TryGetValue(key, out var value) ? value : string.Empty;
        set
        {
            _values[key] = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public IReadOnlyDictionary<string, string> ToValues() => _values;
}
