using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class ProjectEditWindow : Window
{
    private sealed record ResponsibleOption(Guid? Id, string Name);

    /// <summary>Bearbeitbare Zeile der Ansprechpersonen-Tabelle.</summary>
    public sealed class ContactRow
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Responsibility { get; set; } = string.Empty;
    }

    private readonly AppServices _services;
    private readonly Guid? _projectId;
    private readonly ObservableCollection<ContactRow> _contacts = [];

    public ProjectEditWindow(AppServices services, Guid? projectId)
    {
        _services = services;
        _projectId = projectId;
        InitializeComponent();

        StatusBox.ItemsSource = ProjectStatusCatalog.Selectable;
        ResponsibleBox.ItemsSource = BuildResponsibleOptions();
        ContactsGrid.ItemsSource = _contacts;

        if (projectId is null)
        {
            HeadingText.Text = "Neues Projekt anlegen";
            StatusBox.SelectedValue = ProjectStatus.Active;
            NumberBox.Text = string.Empty;
            Loaded += (_, _) => NameBox.Focus();
        }
        else
        {
            HeadingText.Text = "Projekt bearbeiten";
            LoadProject(projectId.Value);
        }
    }

    private IReadOnlyList<ResponsibleOption> BuildResponsibleOptions()
    {
        var options = new List<ResponsibleOption> { new(null, "– niemand –") };
        try
        {
            options.AddRange(_services.Projects.GetResponsibleChoices().Select(user =>
                new ResponsibleOption(
                    user.Id,
                    string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName)));
        }
        catch (LiftUpException exception)
        {
            ErrorText.Text = exception.Message;
        }

        return options;
    }

    private void LoadProject(Guid projectId)
    {
        try
        {
            var project = _services.Projects.GetProject(projectId);
            NameBox.Text = project.Name;
            NumberBox.Text = project.Number ?? string.Empty;
            CustomerBox.Text = project.Customer ?? string.Empty;
            StreetBox.Text = project.CustomerStreet ?? string.Empty;
            AddressLine2Box.Text = project.CustomerLine2 ?? string.Empty;
            ZipBox.Text = project.CustomerZip ?? string.Empty;
            CityBox.Text = project.CustomerCity ?? string.Empty;
            ClientBox.Text = project.Client ?? string.Empty;
            ClientStreetBox.Text = project.ClientStreet ?? string.Empty;
            ClientLine2Box.Text = project.ClientLine2 ?? string.Empty;
            ClientZipBox.Text = project.ClientZip ?? string.Empty;
            ClientCityBox.Text = project.ClientCity ?? string.Empty;
            DescriptionBox.Text = project.Description ?? string.Empty;
            FolderBox.Text = project.FolderPath;

            _contacts.Clear();
            foreach (var contact in project.Contacts)
            {
                _contacts.Add(new ContactRow
                {
                    Name = contact.Name,
                    Email = contact.Email ?? string.Empty,
                    Phone = contact.Phone ?? string.Empty,
                    Responsibility = contact.Responsibility ?? string.Empty,
                });
            }

            // Projekte aus älteren Versionen kennen nur eine Ansprechperson; sie wird zur ersten Zeile.
            if (_contacts.Count == 0 && !string.IsNullOrWhiteSpace(project.Contact))
            {
                _contacts.Add(new ContactRow { Name = project.Contact });
            }

            if (project.IsArchived)
            {
                StatusBox.ItemsSource = ProjectStatusCatalog.All;
                StatusBox.SelectedValue = ProjectStatus.Archived;
            }
            else
            {
                StatusBox.SelectedValue = project.Status;
            }
            CreateFolderCheck.IsChecked = !Directory.Exists(project.FolderPath);

            ResponsibleBox.SelectedValue = project.ResponsibleUserId;

            if (project.IsArchived)
            {
                StatusBox.IsEnabled = false;
                ArchiveInfoText.Text =
                    $"Archiviert am {project.ArchivedUtc?.ToLocalTime():dd.MM.yyyy HH:mm} von {project.ArchivedBy}. " +
                    "Zum Wiederherstellen die Aktion in der Projektübersicht verwenden.";
                ArchiveInfoText.Visibility = Visibility.Visible;
            }
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

    private void OnAddContactClick(object sender, RoutedEventArgs e)
    {
        var row = new ContactRow();
        _contacts.Add(row);
        ContactsGrid.SelectedItem = row;
        ContactsGrid.ScrollIntoView(row);
        ContactsGrid.CurrentCell = new DataGridCellInfo(row, ContactsGrid.Columns[0]);
        ContactsGrid.BeginEdit();
    }

    private void OnRemoveContactClick(object sender, RoutedEventArgs e)
    {
        if (ContactsGrid.SelectedItem is ContactRow selected)
        {
            _contacts.Remove(selected);
        }
        else if (_contacts.Count > 0)
        {
            _contacts.RemoveAt(_contacts.Count - 1);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        // Eine gerade bearbeitete Zelle übernimmt ihren Text sonst erst beim Verlassen.
        try
        {
            ContactsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ContactsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch (InvalidOperationException)
        {
            // Nichts in Bearbeitung: kein Grund, das Speichern abzubrechen.
        }

        var status = StatusBox.SelectedValue is ProjectStatus selectedStatus
            ? selectedStatus
            : ProjectStatus.Active;

        var input = new ProjectInput
        {
            Name = NameBox.Text,
            Number = NumberBox.Text,
            Customer = CustomerBox.Text,
            CustomerStreet = StreetBox.Text,
            CustomerLine2 = AddressLine2Box.Text,
            CustomerZip = ZipBox.Text,
            CustomerCity = CityBox.Text,
            Client = ClientBox.Text,
            ClientStreet = ClientStreetBox.Text,
            ClientLine2 = ClientLine2Box.Text,
            ClientZip = ClientZipBox.Text,
            ClientCity = ClientCityBox.Text,
            Contacts = _contacts
                .Select(row => new ProjectContactInput
                {
                    Name = row.Name,
                    Email = row.Email,
                    Phone = row.Phone,
                    Responsibility = row.Responsibility,
                })
                .ToArray(),
            Description = DescriptionBox.Text,
            ResponsibleUserId = ResponsibleBox.SelectedValue is Guid responsibleId && responsibleId != Guid.Empty
                ? responsibleId
                : null,
            FolderPath = FolderBox.Text,
            Status = status,
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
