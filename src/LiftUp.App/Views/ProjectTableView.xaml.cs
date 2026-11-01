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
public partial class ProjectTableView : UserControl, ITabFillSource
{
    private sealed record ProjectOption(Guid Id, string DisplayText);

    private sealed record SnippetOption(Guid Id, string Name, string Category)
    {
        public string DisplayText => string.IsNullOrWhiteSpace(Category) ? Name : $"{Name} · {Category}";
    }

    /// <summary>Ein Vorschlag in einer Tabellenzelle: Textbaustein oder Wert aus einer Tabelle.</summary>
    private sealed record CellSuggestion(string DisplayText, string Detail, string Text, Guid? SnippetId);

    private readonly AppServices _services;
    private readonly ProjectTableDefinition _definition;
    private readonly ProjectSelection _selection;
    private readonly ObservableCollection<ProjectTableRowModel> _rows = [];
    private readonly List<SnippetOption> _snippets = [];
    private readonly List<CellSuggestion> _matches = [];
    private readonly Func<Guid, IReadOnlyList<ProjectTableRowInput>>? _draftRows;

    private TextBox? _editingBox;
    private int _slashIndex = -1;
    private int _selectedMatch = -1;

    /// <summary>Ob seit dem letzten Lesen etwas geändert wurde – dann bleibt der Reiter stehen.</summary>
    private bool _dirty;

    /// <param name="showProjectSelector">
    /// <c>false</c>, wenn die Tabelle in einem Reiter mit eigener Projektauswahl steht
    /// (etwa „1. Anlagedaten“ der Analyse); das Projekt kommt dann über <see cref="SelectProject"/>.
    /// </param>
    /// <param name="draftRows">
    /// Zeilen, die ohne gespeicherte Angaben als Vorschlag erscheinen – etwa je Aufzug der
    /// Zustandsanalyse eine Zeile. Gespeichert wird erst auf Knopfdruck.
    /// </param>
    public ProjectTableView(
        AppServices services,
        ProjectTableDefinition definition,
        ProjectSelection selection,
        bool showProjectSelector = true,
        Func<Guid, IReadOnlyList<ProjectTableRowInput>>? draftRows = null)
    {
        _services = services;
        _definition = definition;
        _selection = selection;
        _draftRows = draftRows;

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

        // Die Zustandsanalyse kann in einem anderen Reiter Aufzüge anlegen: Beim Wechsel auf
        // diesen Reiter stehen die Zeilen deshalb sofort da, ohne „Aktualisieren“.
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                RefreshIfUnmodified();
            }
        };
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

            // Mehrzeilige Felder („Massnahme“, „Erläuterung“) bekommen ein Textfeld, das
            // Umbrüche aufnimmt.
            if (column.Multiline)
            {
                RowsGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = header,
                    CellTemplate = MultilineCell(binding, editing: false),
                    CellEditingTemplate = MultilineCell(binding, editing: true),
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

    /// <summary>
    /// Zelle für ein mehrzeiliges Feld: Enter und Shift+Enter setzen einen Umbruch, der Text
    /// bricht in der Breite um. Fertig ist die Zelle mit Tab oder einem Klick daneben.
    /// </summary>
    private static DataTemplate MultilineCell(Binding binding, bool editing)
    {
        var factory = new FrameworkElementFactory(editing ? typeof(TextBox) : typeof(TextBlock));

        if (editing)
        {
            factory.SetBinding(TextBox.TextProperty, binding);
            factory.SetValue(TextBox.AcceptsReturnProperty, true);
            factory.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
            factory.SetValue(TextBox.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            factory.SetValue(TextBox.MinHeightProperty, 60.0);
            factory.SetValue(TextBox.PaddingProperty, new Thickness(4, 2, 4, 2));
        }
        else
        {
            factory.SetBinding(TextBlock.TextProperty, binding);
            factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            factory.SetValue(TextBlock.PaddingProperty, new Thickness(4, 2, 4, 2));
        }

        return new DataTemplate { VisualTree = factory };
    }

    /// <summary>Ob die Tabelle Zeilen hat – die Analyse färbt damit den Reiter „1. Anlagedaten“.</summary>
    public bool HasRows => _rows.Count > 0;

    /// <summary>Meldet, wenn sich der Füllstand ändert – für die Farbe des Haupt-Reiters.</summary>
    public event EventHandler? FillChanged;

    /// <summary>Liest die Zeilen neu – etwa nachdem die Zustandsanalyse einen Aufzug erhielt.</summary>
    public void Reload() => LoadRows();

    /// <summary>
    /// Füllstand der Tabelle für die Farbe des Reiters: rot ohne Angaben, gelb, solange Zellen
    /// fehlen, grün, sobald alle Zellen der Zeilen gefüllt sind.
    /// </summary>
    public TabFill Fill
    {
        get
        {
            if (_rows.Count == 0 || _definition.Columns.Count == 0)
            {
                return TabFill.None;
            }

            // Ungespeicherte Zeilen zählen wie unvollständige: Der Reiter bleibt gelb.
            if (_dirty)
            {
                return TabFill.Partial;
            }

            var filled = 0;
            foreach (var row in _rows)
            {
                foreach (var column in _definition.Columns)
                {
                    if (!string.IsNullOrWhiteSpace(row[column.Key]))
                    {
                        filled++;
                    }
                }
            }

            return filled == 0
                ? TabFill.None
                : filled == _rows.Count * _definition.Columns.Count ? TabFill.Full : TabFill.Partial;
        }
    }

    /// <summary>
    /// Liest die Zeilen neu, sobald der Reiter sichtbar wird. Ungespeicherte Eingaben
    /// bleiben stehen, damit ein Reiterwechsel nichts verwirft.
    /// </summary>
    private void RefreshIfUnmodified()
    {
        if (!_dirty)
        {
            LoadRows();
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

            // Ohne gespeicherte Zeilen entstehen so viele, wie die Zustandsanalyse Aufzüge hat.
            if (_rows.Count == 0 && _draftRows is not null)
            {
                foreach (var draft in _draftRows(project.Id))
                {
                    var model = new ProjectTableRowModel();
                    foreach (var column in _definition.Columns)
                    {
                        model[column.Key] = draft.Values.TryGetValue(column.Key, out var value) ? value : string.Empty;
                    }

                    _rows.Add(model);
                }
            }

            InfoText.Text = _rows.Count == 1
                ? $"{_definition.Title}: 1 Zeile"
                : $"{_definition.Title}: {_rows.Count} Zeilen";
            _dirty = false;
            FillChanged?.Invoke(this, EventArgs.Empty);
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
        _dirty = true;
        FillChanged?.Invoke(this, EventArgs.Empty);
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
        _dirty = true;
        FillChanged?.Invoke(this, EventArgs.Empty);
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

            // Ein gelöschter Aufzug verschwindet auch aus den anderen Tabellen – das soll der
            // Benutzer bestätigen, bevor Daten weg sind.
            if (string.Equals(_definition.Key, ProjectTableCatalog.ZustandsanalyseKey, StringComparison.OrdinalIgnoreCase))
            {
                var loss = _services.ProjectTables.FindLoss(project.Id, inputs);
                if (loss.Any && !ConfirmLoss(loss))
                {
                    InfoText.Text = "Speichern abgebrochen.";
                    return;
                }
            }

            var saved = _services.ProjectTables.Save(project.Id, _definition.Key, inputs);

            _rows.Clear();
            foreach (var row in saved)
            {
                _rows.Add(new ProjectTableRowModel(row));
            }

            _dirty = false;
            InfoText.Text = saved.Count == 1
                ? "Gespeichert: 1 Zeile."
                : $"Gespeichert: {saved.Count} Zeilen.";
            FillChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    /// <summary>
    /// Rückfrage vor dem Löschen: Nennt die Aufzüge und die Tabellen, in denen ihre Daten
    /// mitgelöscht werden.
    /// </summary>
    private bool ConfirmLoss(ProjectTableLoss loss)
    {
        var parts = new List<string>(2);
        if (loss.Investitionsplanung > 0)
        {
            parts.Add($"Investitionsplanung: {loss.Investitionsplanung} Zeile(n)");
        }

        if (loss.Anlagedaten > 0)
        {
            parts.Add($"Anlagedaten: {loss.Anlagedaten} Zeile(n)");
        }

        var message =
            "Diese Aufzüge stehen nicht mehr in der Zustandsanalyse: " +
            string.Join(", ", loss.Lifts) + ".\n\n" +
            "Beim Speichern werden ihre Daten auch hier gelöscht:\n- " +
            string.Join("\n- ", parts) + ".\n\n" +
            "Der zugehörige Reiter unter „4. Fotos“ entfällt; die Fotodateien bleiben im " +
            "Projektordner.\n\nWirklich löschen?";

        return MessageBox.Show(
            Window.GetWindow(this),
            message,
            "Aufzug löschen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
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

        // Eine geänderte Zelle hält den Reiter beim Wechsel stehen – bis zum Speichern.
        // Ein abgebrochener Edit zählt nicht.
        if (e.EditAction == DataGridEditAction.Commit)
        {
            _dirty = true;
            FillChanged?.Invoke(this, EventArgs.Empty);
        }
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
        if (sender is not TextBox box)
        {
            return;
        }

        var caret = Math.Min(box.CaretIndex, box.Text.Length);
        var before = box.Text[..caret];

        // „//Feld“ sucht in den Werten der Tabellen, „/Text“ in den Textbausteinen.
        if (FindTableToken(before) is { } table)
        {
            ShowTableValues(box, table.Start, table.Token);
            return;
        }

        if (_snippets.Count == 0)
        {
            HideSnippets();
            return;
        }

        var slash = before.LastIndexOf('/');

        if (slash < 0 || (slash > 0 && !char.IsWhiteSpace(before[slash - 1])))
        {
            HideSnippets();
            return;
        }

        var token = before[(slash + 1)..];
        if (token.Contains(' ') || token.Contains('/') || token.Length > 40)
        {
            HideSnippets();
            return;
        }

        ShowSnippets(box, slash, token);
    }

    /// <summary>„//Feld“ vor dem Cursor, sofern es am Wortanfang steht.</summary>
    private static (int Start, string Token)? FindTableToken(string before)
    {
        var start = before.LastIndexOf("//", StringComparison.Ordinal);
        if (start < 0 || (start > 0 && !char.IsWhiteSpace(before[start - 1])))
        {
            return null;
        }

        var token = before[(start + 2)..];
        return token.Contains(' ') || token.Contains('/') || token.Length > 40
            ? null
            : (start, token);
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
        _matches.AddRange(_snippets
            .Where(snippet =>
                token.Length == 0
                || snippet.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
                || snippet.Category.Contains(token, StringComparison.OrdinalIgnoreCase))
            .Select(snippet => new CellSuggestion(snippet.DisplayText, string.Empty, string.Empty, snippet.Id)));

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

    /// <summary>
    /// Zeigt die gefüllten Werte der Tabellen („//“) – nur aus dem gewählten Projekt, mit
    /// Herkunft in der Liste. Übernommen wird der reine Text, weil eine Zelle keine
    /// Formatierung kennt.
    /// </summary>
    private void ShowTableValues(TextBox box, int start, string token)
    {
        if (ProjectBox.SelectedItem is not ProjectOption project)
        {
            HideSnippets();
            return;
        }

        _matches.Clear();
        try
        {
            var tables = ProjectTableCatalog.Known
                .Select(definition => (definition, _services.ProjectTables.Get(project.Id, definition.Key)))
                .ToArray();

            _matches.AddRange(ProjectTableValues.Search(tables, token, projectId: project.Id)
                .Select(value => new CellSuggestion(value.Text, value.Detail, value.Text, null)));
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }

        _slashIndex = start;
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

            // Zwei Zeilen wie im Dokument-Editor: oben der Wert, darunter Spalte und Herkunft
            // („Jahr · Investitionsplanung“); der Tooltip bleibt nur Zugabe.
            var texts = new StackPanel { Orientation = Orientation.Vertical };
            texts.Children.Add(new TextBlock
            {
                Text = match.DisplayText,
                MaxWidth = 300,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (!string.IsNullOrWhiteSpace(match.Detail))
            {
                texts.Children.Add(new TextBlock
                {
                    Text = match.Detail,
                    FontSize = 12,
                    MaxWidth = 300,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x86)),
                });
            }

            var row = new Button
            {
                Content = texts,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(0),
                Background = index == _selectedMatch
                    ? new SolidColorBrush(Color.FromRgb(0xE3, 0xEE, 0xFA))
                    : Brushes.Transparent,
                ToolTip = string.IsNullOrWhiteSpace(match.Detail)
                    ? match.DisplayText
                    : $"{match.DisplayText} · {match.Detail}",
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
    private void PickSnippet(CellSuggestion suggestion)
    {
        var box = _editingBox;
        if (box is null || _slashIndex < 0)
        {
            HideSnippets();
            return;
        }

        string text;
        if (suggestion.SnippetId is { } id)
        {
            try
            {
                text = ToPlainText(_services.Snippets.Get(id).Html);
            }
            catch (LiftUpException exception)
            {
                InfoText.Text = exception.Message;
                HideSnippets();
                return;
            }
        }
        else
        {
            text = suggestion.Text;
        }

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
