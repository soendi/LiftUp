using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly ObservableCollection<MilestoneRow> _milestones = [];
    private Guid? _milestoneProjectId;
    private bool _milestonesLoading;

    public ProjectsView(AppServices services)
    {
        _services = services;
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
    }

    /// <summary>Zeigt die Meilensteine des gewählten Projekts auf der rechten Seite.</summary>
    private void LoadMilestones(ProjectListItem? project)
    {
        _milestonesLoading = true;
        _milestoneProjectId = project?.Id;
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
                _milestones.Add(ToRow(milestone));
            }

            // Die Begehung ist fester Bestandteil jedes Projekts.
            if (!_milestones.Any(row => row.IsFixed))
            {
                _milestones.Insert(0, new MilestoneRow
                {
                    Title = ProjectMilestoneCatalog.Inspection,
                    IsFixed = true,
                });
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
        _milestones.Add(row);
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

    private void OnMilestoneRowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        if (_milestonesLoading || e.EditAction != DataGridEditAction.Commit)
        {
            return;
        }

        // Erst speichern, wenn die Zelle übernommen ist.
        Dispatcher.BeginInvoke(new Action(SaveMilestones));
    }

    /// <summary>Schreibt die Liste direkt in die Datenbank; ohne eigenes Speichern-Knopf.</summary>
    private void SaveMilestones()
    {
        if (_milestonesLoading || _milestoneProjectId is not Guid projectId)
        {
            return;
        }

        MilestonesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        MilestonesGrid.CommitEdit(DataGridEditingUnit.Row, true);

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
            _services.Projects.SaveMilestones(projectId, inputs);
            LoadMilestones(Selected);
            MilestoneInfoText.Text = "Meilensteine gespeichert.";
        }
        catch (LiftUpException exception)
        {
            MilestoneInfoText.Text = exception.Message;
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
