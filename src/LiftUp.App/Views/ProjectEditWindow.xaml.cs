using System.IO;
using System.Windows;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class ProjectEditWindow : Window
{
    private readonly AppServices _services;
    private readonly Guid? _projectId;

    public ProjectEditWindow(AppServices services, Guid? projectId)
    {
        _services = services;
        _projectId = projectId;
        InitializeComponent();

        StatusBox.ItemsSource = ProjectStatusCatalog.All;

        if (projectId is null)
        {
            HeadingText.Text = "Neues Projekt anlegen";
            StatusBox.SelectedValue = ProjectStatus.Active;
            Loaded += (_, _) => NameBox.Focus();
        }
        else
        {
            HeadingText.Text = "Projekt bearbeiten";
            LoadProject(projectId.Value);
        }
    }

    private void LoadProject(Guid projectId)
    {
        try
        {
            var project = _services.Projects.GetProject(projectId);
            NameBox.Text = project.Name;
            NumberBox.Text = project.Number ?? string.Empty;
            CustomerBox.Text = project.Customer ?? string.Empty;
            DescriptionBox.Text = project.Description ?? string.Empty;
            FolderBox.Text = project.FolderPath;
            StatusBox.SelectedValue = project.Status;
            CreateFolderCheck.IsChecked = !Directory.Exists(project.FolderPath);
        }
        catch (LiftUpException exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Projektordner wählen",
            Multiselect = false,
        };

        var current = FolderBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            ErrorText.Text = string.Empty;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        var input = new ProjectInput
        {
            Name = NameBox.Text,
            Number = NumberBox.Text,
            Customer = CustomerBox.Text,
            Description = DescriptionBox.Text,
            FolderPath = FolderBox.Text,
            Status = StatusBox.SelectedValue is ProjectStatus status ? status : ProjectStatus.Active,
            CreateFolderIfMissing = CreateFolderCheck.IsChecked == true,
        };

        try
        {
            if (_projectId is null)
            {
                _services.Projects.Create(input);
            }
            else
            {
                _services.Projects.Update(_projectId.Value, input);
            }

            DialogResult = true;
        }
        catch (ValidationException exception)
        {
            ErrorText.Text = exception.Message;
        }
        catch (LiftUpException exception)
        {
            ErrorText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            ErrorText.Text = "Das Projekt konnte nicht gespeichert werden: " + exception.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
