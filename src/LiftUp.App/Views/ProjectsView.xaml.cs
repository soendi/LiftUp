using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class ProjectsView : UserControl
{
    private readonly AppServices _services;

    public ProjectsView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var session = _services.CurrentSession;
        NewButton.IsEnabled = session.Can(Permission.CreateProjects);
        EditButton.IsEnabled = session.Can(Permission.EditProjects);
        DeleteButton.IsEnabled = session.Can(Permission.DeleteProjects);

        LoadProjects();
    }

    private void LoadProjects()
    {
        try
        {
            var items = _services.Projects.Search(SearchBox.Text);
            ProjectsGrid.ItemsSource = items;
            InfoText.Text = items.Count == 1
                ? "1 Projekt"
                : $"{items.Count} Projekte";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private ProjectListItem? Selected => ProjectsGrid.SelectedItem as ProjectListItem;

    private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadProjects();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => LoadProjects();

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
