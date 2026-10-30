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

            ProjectsGrid.ItemsSource = items;
            UpdateInfoText(items.Count);
            UpdateButtons();
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

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

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
