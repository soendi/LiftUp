using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class ProjectsView : UserControl
{
    private sealed record StatusOption(ProjectStatus? Value, string Name);

    private sealed record SortOption(ProjectSort Value, string Name);

    private static readonly StatusOption[] StatusOptions =
    [
        new(null, "Alle Status"),
        .. ProjectStatusCatalog.Selectable.Select(info => new StatusOption(info.Value, info.Name)),
    ];

    private static readonly SortOption[] SortOptions =
    [
        new(ProjectSort.Name, "Projekt"),
        new(ProjectSort.Number, "Nummer"),
        new(ProjectSort.Customer, "Kunde"),
        new(ProjectSort.Responsible, "Verantwortlich"),
        new(ProjectSort.Status, "Status"),
        new(ProjectSort.Updated, "Geändert"),
    ];

    private readonly AppServices _services;
    private readonly ProjectSelection _selection;
    private readonly ObservableCollection<MilestoneRow> _milestones = [];
    private Guid? _milestoneProjectId;
    private bool _milestonesLoading;
    private bool _milestonesSaving;
    private DataGridRow? _previewRow;
    private string _previewPath = string.Empty;

    public ProjectsView(AppServices services, ProjectSelection selection)
    {
        _services = services;
        _selection = selection;
        InitializeComponent();

        StatusFilterBox.ItemsSource = StatusOptions;
        StatusFilterBox.SelectedIndex = 0;
        SortBox.ItemsSource = SortOptions;
        SortBox.SelectedIndex = 0;

        var session = _services.CurrentSession;
        NewButton.IsEnabled = session.Can(Permission.CreateProjects);
        EditButton.IsEnabled = session.Can(Permission.EditProjects);
        DeleteButton.IsEnabled = session.Can(Permission.DeleteProjects);
        ArchiveButton.IsEnabled = session.Can(Permission.ArchiveProjects);
        RestoreButton.IsEnabled = session.Can(Permission.ArchiveProjects);
        ArchiveToggle.IsEnabled = session.Can(Permission.ViewArchivedProjects);

        MilestonesGrid.ItemsSource = _milestones;
        MilestonesGrid.IsReadOnly = !session.Can(Permission.EditProjects);
        MilestonesGrid.BeginningEdit += OnMilestoneBeginningEdit;
        MilestonesGrid.RowEditEnding += OnMilestoneRowEditEnding;

        ProjectsGrid.MouseMove += OnProjectsGridMouseMove;
        ProjectsGrid.MouseLeave += (_, _) => HideImagePreview();
        ProjectsGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => HideImagePreview()));

        LoadProjects();
    }

    private bool ShowArchive => ArchiveToggle.IsChecked == true;

    private void LoadProjects()
    {
        try
        {
            var items = _services.Projects.Search(new ProjectQuery
            {
                Search = SearchBox.Text,
                Status = (StatusFilterBox.SelectedItem as StatusOption)?.Value,
                Scope = ShowArchive ? ProjectScope.Archived : ProjectScope.Active,
                Sort = (SortBox.SelectedItem as SortOption)?.Value ?? ProjectSort.Name,
            });

            var keepId = Selected?.Id;
            HideImagePreview();
            ProjectsGrid.ItemsSource = items;

            // Nach dem Neuladen bleibt das Projekt gewählt, damit die Meilensteine stehen bleiben.
            if (keepId is not null)
            {
                ProjectsGrid.SelectedItem = items.FirstOrDefault(item => item.Id == keepId);
            }

            UpdateInfoText(items.Count);
            UpdateButtons();
            LoadMilestones(Selected);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void UpdateInfoText(int count)
    {
        var scope = ShowArchive ? "archivierte" : "aktive";
        InfoText.Text = count == 1
            ? $"1 {scope}s Projekt"
            : $"{count} {scope} Projekte";
    }

    private ProjectListItem? Selected => ProjectsGrid.SelectedItem as ProjectListItem;

    private void UpdateButtons()
    {
        var selected = Selected;

        EditButton.IsEnabled = selected is not null && _services.CurrentSession.Can(Permission.EditProjects);
        DeleteButton.IsEnabled = selected is not null && _services.CurrentSession.Can(Permission.DeleteProjects);

        var mayArchive = _services.CurrentSession.Can(Permission.ArchiveProjects);
        ArchiveButton.IsEnabled = mayArchive && selected is not null && !selected.IsArchived;
        RestoreButton.IsEnabled = mayArchive && selected is not null && selected.IsArchived;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadProjects();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => LoadProjects();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => LoadProjects();

    private void OnArchiveToggleClick(object sender, RoutedEventArgs e) => LoadProjects();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        LoadMilestones(Selected);
        HideImagePreview();
    }

    /// <summary>Zeigt das Projektbild der Zeile unter dem Mauszeiger in einem eigenen Fenster.</summary>
    private void OnProjectsGridMouseMove(object sender, MouseEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject);
        if (row?.Item is not ProjectListItem item)
        {
            HideImagePreview();
            return;
        }

        if (ReferenceEquals(row, _previewRow) && ImagePopup.IsOpen)
        {
            return;
        }

        ShowImagePreview(row, item);
    }

    private void ShowImagePreview(DataGridRow row, ProjectListItem item)
    {
        _previewRow = row;

        var path = item.ImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            HideImagePreview();
            return;
        }

        if (!string.Equals(path, _previewPath, StringComparison.OrdinalIgnoreCase) || ImagePreview.Source is null)
        {
            try
            {
                // Ohne OnLoad hält die Vorschau die Datei gesperrt.
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                ImagePreview.Source = image;
            }
            catch (Exception exception) when (
                exception is NotSupportedException or UriFormatException or IOException or FileFormatException)
            {
                HideImagePreview();
                MilestoneInfoText.Text = "Das Projektbild konnte nicht geladen werden: " + exception.Message;
                return;
            }

            _previewPath = path;
        }

        ImageCaption.Text = Path.GetFileName(path);
        ImagePopup.PlacementTarget = row;
        ImagePopup.Placement = PlacementMode.Right;
        ImagePopup.HorizontalOffset = 8;
        ImagePopup.VerticalOffset = 0;
        ImagePopup.IsOpen = true;
    }

    private void HideImagePreview()
    {
        _previewRow = null;
        _previewPath = string.Empty;
        ImagePopup.IsOpen = false;
        ImagePreview.Source = null;
    }

    private static DataGridRow? FindRow(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is DataGridRow row)
            {
                return row;
            }

            // Nur visuelle Elemente haben einen visuellen Elternteil; bei Textinhalten
            // würde VisualTreeHelper sonst eine Ausnahme werfen.
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : null;
        }

        return null;
    }

    /// <summary>Zeigt die Meilensteine des gewählten Projekts auf der rechten Seite.</summary>
    private void LoadMilestones(ProjectListItem? project)
    {
        _milestonesLoading = true;
        _milestoneProjectId = project?.Id;

        // Eine offene Bearbeitung beenden, sonst wehrt sich die Tabelle gegen das Leeren.
        EndGridEditing(MilestonesGrid);
        _milestones.Clear();

        if (project is null)
        {
            MilestoneProjectText.Text = "Kein Projekt gewählt";
            MilestoneInfoText.Text = "Bitte links ein Projekt auswählen.";
            _milestonesLoading = false;
            return;
        }

        MilestoneProjectText.Text = $"{project.Number} · {project.Name}";

        try
        {
            foreach (var milestone in _services.Projects.GetProject(project.Id).Milestones)
            {
                AddMilestoneRow(ToRow(milestone));
            }

            // Die Begehung ist fester Bestandteil jedes Projekts.
            if (!_milestones.Any(row => row.IsFixed))
            {
                AddMilestoneRow(
                    new MilestoneRow
                    {
                        Title = ProjectMilestoneCatalog.Inspection,
                        IsFixed = true,
                    },
                    index: 0);
            }

            MilestoneInfoText.Text = _milestones.Count == 1
                ? "1 Meilenstein"
                : $"{_milestones.Count} Meilensteine";
        }
        catch (LiftUpException exception)
        {
            MilestoneInfoText.Text = exception.Message;
        }
        finally
        {
            _milestonesLoading = false;
        }
    }

    private static MilestoneRow ToRow(ProjectMilestone milestone) => new()
    {
        Title = milestone.Title,
        IsDone = milestone.IsDone,
        DoneDateText = milestone.DoneDate?.ToString("dd.MM.yyyy") ?? string.Empty,
        IsFixed = string.Equals(
            milestone.Title, ProjectMilestoneCatalog.Inspection, StringComparison.OrdinalIgnoreCase),
    };

    private void OnAddMilestoneClick(object sender, RoutedEventArgs e)
    {
        if (_milestoneProjectId is not Guid)
        {
            MilestoneInfoText.Text = "Bitte links ein Projekt auswählen.";
            return;
        }

        if (!_services.CurrentSession.Can(Permission.EditProjects))
        {
            MilestoneInfoText.Text = "Für Änderungen an den Meilensteinen fehlt das Recht.";
            return;
        }

        var row = new MilestoneRow();
        AddMilestoneRow(row);
        MilestonesGrid.SelectedItem = row;
        MilestonesGrid.ScrollIntoView(row);
        MilestonesGrid.CurrentCell = new DataGridCellInfo(row, MilestonesGrid.Columns[0]);
        MilestonesGrid.BeginEdit();
        MilestoneInfoText.Text = "Bezeichnung eintragen – beim Verlassen der Zeile wird gespeichert.";
    }

    private void OnRemoveMilestoneClick(object sender, RoutedEventArgs e)
    {
        if (MilestonesGrid.SelectedItem is not MilestoneRow row)
        {
            MilestoneInfoText.Text = "Bitte zuerst eine Zeile auswählen.";
            return;
        }

        if (row.IsFixed)
        {
            MilestoneInfoText.Text =
                $"Der Meilenstein „{ProjectMilestoneCatalog.Inspection}“ ist fest vorgegeben und lässt sich nicht entfernen.";
            return;
        }

        _milestones.Remove(row);
        SaveMilestones();
    }

    /// <summary>Die Bezeichnung der Begehung bleibt unverändert; Erledigt und Datum sind frei.</summary>
    private void OnMilestoneBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is MilestoneRow { IsFixed: true } && e.Column == MilestonesGrid.Columns[0])
        {
            e.Cancel = true;
            MilestoneInfoText.Text =
                $"Der Meilenstein „{ProjectMilestoneCatalog.Inspection}“ ist fest vorgegeben";
        }
    }

    /// <summary>
    /// Hängt eine Zeile ein und horcht auf den erledigt-Haken. Ein Klick auf das Kästchen
    /// beendet die Zeilenbearbeitung nicht – gespeichert wird deshalb schon bei der Änderung.
    /// </summary>
    private void AddMilestoneRow(MilestoneRow row, int index = -1)
    {
        row.PropertyChanged += OnMilestoneRowChanged;

        if (index < 0)
        {
            _milestones.Add(row);
        }
        else
        {
            _milestones.Insert(index, row);
        }
    }

    /// <summary>Speichert den erledigt-Haken sofort; Texte weiterhin beim Verlassen der Zeile.</summary>
    private void OnMilestoneRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_milestonesLoading || _milestonesSaving || e.PropertyName != nameof(MilestoneRow.IsDone))
        {
            return;
        }

        SaveMilestones();
    }

    private void OnMilestoneRowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        if (_milestonesLoading || e.EditAction != DataGridEditAction.Commit)
        {
            return;
        }

        // Erst speichern, wenn die Zelle übernommen ist; sonst greift das Speichern mitten
        // in die Bearbeitung ein.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(SaveMilestones));
    }

    /// <summary>Schreibt die Liste direkt in die Datenbank; ohne eigenes Speichern-Knopf.</summary>
    private void SaveMilestones()
    {
        if (_milestonesLoading || _milestonesSaving || _milestoneProjectId is not Guid projectId)
        {
            return;
        }

        _milestonesSaving = true;
        try
        {
            // Die Zelle muss übernommen sein, bevor die Werte gelesen werden. Während eines
            // laufenden Commits darf nicht erneut committet werden.
            CommitGridEdits(MilestonesGrid);

            var inputs = new List<ProjectMilestoneInput>();
            foreach (var row in _milestones)
            {
                var title = (row.Title ?? string.Empty).Trim();
                if (title.Length == 0 && !row.IsDone && string.IsNullOrWhiteSpace(row.DoneDateText))
                {
                    // Eine gerade begonnene Zeile wird erst beim Ausfüllen gespeichert.
                    continue;
                }

                DateTime? date;
                try
                {
                    date = ParseMilestoneDate(row.DoneDateText);
                }
                catch (ValidationException exception)
                {
                    MilestoneInfoText.Text = exception.Message;
                    return;
                }

                inputs.Add(new ProjectMilestoneInput
                {
                    Title = title,
                    IsDone = row.IsDone,
                    DoneDate = date,
                });
            }

            try
            {
                var saved = _services.Projects.SaveMilestones(projectId, inputs);
                ApplySavedMilestones(saved.Milestones);
                MilestoneInfoText.Text = "Meilensteine gespeichert.";
            }
            catch (LiftUpException exception)
            {
                MilestoneInfoText.Text = exception.Message;
            }
            catch (Exception exception)
            {
                MilestoneInfoText.Text =
                    "Die Meilensteine konnten nicht gespeichert werden: " + exception.Message;
            }
        }
        finally
        {
            _milestonesSaving = false;
        }
    }

    /// <summary>
    /// Übernimmt das Gespeicherte in die vorhandenen Zeilen. Die Liste wird bewusst nicht
    /// neu aufgebaut: Eine gebundene Sammlung mitten in der Bearbeitung zu leeren, beendet
    /// die Anwendung mit einer Ausnahme aus dem DataGrid.
    /// </summary>
    private void ApplySavedMilestones(IReadOnlyList<ProjectMilestone> saved)
    {
        if (saved.Count != _milestones.Count)
        {
            // Andere Anzahl (etwa leere Zeile oder fehlende Begehung): erst nach dem
            // Abschluss der Bearbeitung neu laden.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ReloadMilestones));
            return;
        }

        for (var index = 0; index < saved.Count; index++)
        {
            var target = _milestones[index];
            var source = saved[index];
            target.Title = source.Title;
            target.IsDone = source.IsDone;
            target.DoneDateText = source.DoneDate?.ToString("dd.MM.yyyy") ?? string.Empty;
            target.IsFixed = string.Equals(
                source.Title, ProjectMilestoneCatalog.Inspection, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Lädt die Meilensteine neu, ohne dass eine Bearbeitung offen bleibt.</summary>
    private void ReloadMilestones()
    {
        if (_milestoneProjectId is null)
        {
            return;
        }

        EndGridEditing(MilestonesGrid);
        LoadMilestones(Selected);
    }

    /// <summary>Übernimmt offene Zellen; ohne Bearbeitung passiert nichts.</summary>
    private static void CommitGridEdits(DataGrid grid)
    {
        try
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch (InvalidOperationException)
        {
            // Gerade keine (oder bereits laufende) Bearbeitung: kein Grund abzubrechen.
        }
    }

    /// <summary>Beendet eine offene Bearbeitung, damit die Tabelle neu gefüllt werden darf.</summary>
    private static void EndGridEditing(DataGrid grid)
    {
        try
        {
            grid.CancelEdit(DataGridEditingUnit.Cell);
            grid.CancelEdit(DataGridEditingUnit.Row);
        }
        catch (InvalidOperationException)
        {
            // Keine Bearbeitung offen.
        }
    }

    /// <summary>Datum wie im Projektdialog: leer heißt offen, sonst TT.MM.JJJJ.</summary>
    private static DateTime? ParseMilestoneDate(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "yyyy-MM-dd"];
        if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.Date;
        }

        throw new ValidationException($"Das Datum „{trimmed}“ ist ungültig; bitte TT.MM.JJJJ verwenden.");
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ProjectEditWindow(_services, null) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            LoadProjects();
            ShowInOtherTabs(dialog.SavedProjectId);
        }
    }

    /// <summary>
    /// Meldet den neuen Stand an die Reiter mit eigener Projektauswahl: zuerst die Liste,
    /// damit das Projekt überall vorhanden ist, danach die Auswahl darauf.
    /// </summary>
    private void ShowInOtherTabs(Guid? projectId)
    {
        _selection.NotifyProjectsChanged();

        if (projectId is { } id)
        {
            _selection.Select(id);
        }
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            InfoText.Text = "Bitte zuerst ein Projekt auswählen.";
            return;
        }

        var dialog = new ProjectEditWindow(_services, selected.Id) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            LoadProjects();
            // Name und Nummer können sich geändert haben; die Reiter lesen die Liste neu ein.
            _selection.NotifyProjectsChanged();
        }
    }

    private void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            InfoText.Text = "Bitte zuerst ein Projekt auswählen.";
            return;
        }

        var answer = MessageBox.Show(
            $"Projekt \"{selected.Name}\" archivieren?\n\n" +
            "Nur der Status ändert sich. Dateien und Ordner bleiben unverändert.",
            "Projekt archivieren", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Projects.Archive(selected.Id);
            LoadProjects();
        }
        catch (LiftUpException exception)
        {
            MessageBox.Show(exception.Message, "Projekt archivieren", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            InfoText.Text = "Bitte zuerst ein Projekt auswählen.";
            return;
        }

        try
        {
            _services.Projects.Restore(selected.Id);
            LoadProjects();
        }
        catch (LiftUpException exception)
        {
            MessageBox.Show(exception.Message, "Projekt wiederherstellen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            InfoText.Text = "Bitte zuerst ein Projekt auswählen.";
            return;
        }

        var answer = MessageBox.Show(
            $"Projekt \"{selected.Name}\" wirklich löschen?\n\nDer Datensatz wird entfernt, der Projektordner bleibt zunächst erhalten.",
            "Projekt löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var moveFolder = false;
        if (Directory.Exists(selected.FolderPath))
        {
            var folderAnswer = MessageBox.Show(
                $"Soll der Projektordner\n{selected.FolderPath}\nzusätzlich in den Papierkorb verschoben werden?",
                "Projektordner", MessageBoxButton.YesNo, MessageBoxImage.Question);
            moveFolder = folderAnswer == MessageBoxResult.Yes;
        }

        try
        {
            var warning = _services.Projects.Delete(selected.Id, moveFolder);
            LoadProjects();
            if (warning is not null)
            {
                MessageBox.Show(warning, "Projektordner", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (LiftUpException exception)
        {
            MessageBox.Show(exception.Message, "Projekt löschen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null && EditButton.IsEnabled)
        {
            OnEditClick(sender, e);
        }
    }
}
