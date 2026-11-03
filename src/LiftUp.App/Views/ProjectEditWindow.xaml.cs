using System.Collections.ObjectModel;
using System.Globalization;
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

    /// <summary>Projekt, das zuletzt gespeichert wurde – die Reiter stellen ihre Auswahl darauf ein.</summary>
    public Guid? SavedProjectId { get; private set; }

    private readonly AppServices _services;
    private readonly Guid? _projectId;
    private readonly ObservableCollection<ContactRow> _contacts = [];
    private readonly ObservableCollection<ContactRow> _clientContacts = [];
    private readonly ObservableCollection<MilestoneRow> _milestones = [];

    public ProjectEditWindow(AppServices services, Guid? projectId)
    {
        _services = services;
        _projectId = projectId;
        InitializeComponent();

        StatusBox.ItemsSource = ProjectStatusCatalog.Selectable;
        ResponsibleBox.ItemsSource = BuildResponsibleOptions();
        ContactsGrid.ItemsSource = _contacts;
        ClientContactsGrid.ItemsSource = _clientContacts;
        MilestonesGrid.ItemsSource = _milestones;
        MilestonesGrid.BeginningEdit += OnMilestoneBeginningEdit;

        if (projectId is null)
        {
            HeadingText.Text = "Neues Projekt anlegen";
            StatusBox.SelectedValue = ProjectStatus.Active;
            NumberBox.Text = string.Empty;
            // Die Begehung liefert das Datum für das Deckblatt und ist fest vorgegeben.
            _milestones.Add(new MilestoneRow { Title = ProjectMilestoneCatalog.Inspection, IsFixed = true });
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
            ImageBox.Text = project.ImagePath ?? string.Empty;

            _contacts.Clear();
            foreach (var contact in project.Contacts)
            {
                _contacts.Add(CreateRow(contact));
            }

            // Projekte aus älteren Versionen kennen nur eine Ansprechperson; sie wird zur ersten Zeile.
            if (_contacts.Count == 0 && !string.IsNullOrWhiteSpace(project.Contact))
            {
                _contacts.Add(new ContactRow { Name = project.Contact });
            }

            _clientContacts.Clear();
            foreach (var contact in project.ClientContacts)
            {
                _clientContacts.Add(CreateRow(contact));
            }

            _milestones.Clear();
            foreach (var milestone in project.Milestones)
            {
                _milestones.Add(new MilestoneRow
                {
                    Title = milestone.Title,
                    IsDone = milestone.IsDone,
                    DoneDateText = milestone.DoneDate?.ToString("dd.MM.yyyy") ?? string.Empty,
                    IsFixed = string.Equals(
                        milestone.Title, ProjectMilestoneCatalog.Inspection, StringComparison.OrdinalIgnoreCase),
                });
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

    private static ContactRow CreateRow(ProjectContact contact) => new()
    {
        Name = contact.Name,
        Email = contact.Email ?? string.Empty,
        Phone = contact.Phone ?? string.Empty,
        Responsibility = contact.Responsibility ?? string.Empty,
    };

    /// <summary>
    /// Bild für das Deckblatt wählen. Liegt der Projektordner schon, startet die Auswahl im
    /// Bilderordner des Projekts; bei einem neuen Projekt im Ordner der zuletzt gewählten Datei.
    /// </summary>
    private void OnBrowseImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Projektbild wählen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.svg;*.tif;*.tiff|Alle Dateien|*.*",
            CheckFileExists = true,
        };

        var folder = FolderBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            var images = Path.Combine(folder, ProjectFolders.Images);
            dialog.InitialDirectory = Directory.Exists(images) ? images : folder;
        }
        else
        {
            var selected = ImageBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(selected) && File.Exists(selected))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(selected);
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            ImageBox.Text = dialog.FileName;
            ErrorText.Text = string.Empty;
        }
    }

    /// <summary>
    /// Straßenansicht zur Projektadresse: im Vollbild ein Foto aufnehmen, zuschneiden und als
    /// Projektbild eintragen. Gespeichert wird es wie jedes andere Bild – beim Speichern des
    /// Projekts landet die Datei im Bilderordner des Projekts.
    /// </summary>
    private void OnStreetViewClick(object sender, RoutedEventArgs e)
    {
        var address = StreetViewLink.BuildAddress(
            StreetBox.Text, AddressLine2Box.Text, ZipBox.Text, CityBox.Text);
        if (address.Length == 0)
        {
            address = StreetViewLink.BuildAddress(
                ClientStreetBox.Text, ClientLine2Box.Text, ClientZipBox.Text, ClientCityBox.Text);
        }

        if (address.Length == 0)
        {
            ErrorText.Text = "Für die Straßenansicht fehlt die Adresse: bitte unter „Projekt“ " +
                             "Straße, PLZ und Ort eintragen.";
            return;
        }

        var browser = new StreetViewWindow(
            address, LiftUp.Core.Services.StreetViewSettings.LoadApiKey(_services.Database))
        {
            Owner = this,
        };
        if (browser.ShowDialog() != true || browser.ImagePath is not { } photo)
        {
            return;
        }

        try
        {
            var crop = new ImageCropWindow(photo) { Owner = this };
            if (crop.ShowDialog() != true || crop.Result is null)
            {
                return;
            }

            var target = Path.Combine(Path.GetTempPath(), $"StreetView-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            ImageCropWindow.SavePng(crop.Result, target);
            ImageBox.Text = target;
            ErrorText.Text = string.Empty;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException
                or System.Runtime.InteropServices.COMException)
        {
            ErrorText.Text = "Das Foto konnte nicht übernommen werden: " + exception.Message;
        }
        finally
        {
            TryDeleteFile(photo);
        }
    }

    /// <summary>Räumt die Zwischendatei auf; ein Fehler dabei darf den Ablauf nicht stören.</summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Die Datei liegt im temporären Ordner; sie darf dort liegen bleiben.
        }
    }

    private void OnAddContactClick(object sender, RoutedEventArgs e) =>
        AddRow(ContactsGrid, _contacts);

    private void OnRemoveContactClick(object sender, RoutedEventArgs e) =>
        RemoveRow(ContactsGrid, _contacts);

    private void OnAddClientContactClick(object sender, RoutedEventArgs e) =>
        AddRow(ClientContactsGrid, _clientContacts);

    private void OnAddMilestoneClick(object sender, RoutedEventArgs e)
    {
        var row = new MilestoneRow();
        _milestones.Add(row);
        MilestonesGrid.SelectedItem = row;
        MilestonesGrid.ScrollIntoView(row);
        MilestonesGrid.CurrentCell = new DataGridCellInfo(row, MilestonesGrid.Columns[0]);
        MilestonesGrid.BeginEdit();
    }

    private void OnRemoveMilestoneClick(object sender, RoutedEventArgs e)
    {
        if (MilestonesGrid.SelectedItem is MilestoneRow { IsFixed: true })
        {
            ErrorText.Text = $"Der Meilenstein „{ProjectMilestoneCatalog.Inspection}“ ist fest vorgegeben " +
                             "und lässt sich nicht entfernen.";
            return;
        }

        if (MilestonesGrid.SelectedItem is MilestoneRow selected)
        {
            _milestones.Remove(selected);
        }
        else if (_milestones.Count > 0 && _milestones[^1] is { IsFixed: false } last)
        {
            _milestones.Remove(last);
        }
    }

    /// <summary>Die Bezeichnung der Begehung bleibt unverändert; Erledigt und Datum sind frei.</summary>
    private void OnMilestoneBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is MilestoneRow { IsFixed: true } && e.Column == MilestonesGrid.Columns[0])
        {
            e.Cancel = true;
            ErrorText.Text = $"Der Meilenstein „{ProjectMilestoneCatalog.Inspection}“ ist fest vorgegeben.";
        }
    }

    /// <summary>Liest das Datum aus der Tabelle; leer heißt „kein Datum“.</summary>
    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (DateTime.TryParseExact(value, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
        {
            return exact.Date;
        }

        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed.Date;
        }

        throw new ValidationException($"Das Datum \"{value}\" ist ungültig. Bitte TT.MM.JJJJ verwenden.");
    }

    private void OnRemoveClientContactClick(object sender, RoutedEventArgs e) =>
        RemoveRow(ClientContactsGrid, _clientContacts);

    /// <summary>Neue Zeile anlegen und gleich die Namenszelle bearbeiten.</summary>
    private static void AddRow(DataGrid grid, ObservableCollection<ContactRow> rows)
    {
        var row = new ContactRow();
        rows.Add(row);
        grid.SelectedItem = row;
        grid.ScrollIntoView(row);
        grid.CurrentCell = new DataGridCellInfo(row, grid.Columns[0]);
        grid.BeginEdit();
    }

    private static void RemoveRow(DataGrid grid, ObservableCollection<ContactRow> rows)
    {
        if (grid.SelectedItem is ContactRow selected)
        {
            rows.Remove(selected);
        }
        else if (rows.Count > 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }

    private static ProjectContactInput[] ToInputs(ObservableCollection<ContactRow> rows, ProjectContactKind kind) =>
        rows.Select(row => new ProjectContactInput
        {
            Kind = kind,
            Name = row.Name,
            Email = row.Email,
            Phone = row.Phone,
            Responsibility = row.Responsibility,
        }).ToArray();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        // Eine gerade bearbeitete Zelle übernimmt ihren Text sonst erst beim Verlassen.
        CommitEdits(ContactsGrid);
        CommitEdits(ClientContactsGrid);
        CommitEdits(MilestonesGrid);

        ProjectMilestoneInput[] milestones;
        try
        {
            milestones = _milestones
                .Select(row => new ProjectMilestoneInput
                {
                    Title = row.Title,
                    IsDone = row.IsDone,
                    DoneDate = ParseDate(row.DoneDateText),
                })
                .ToArray();
        }
        catch (ValidationException exception)
        {
            ErrorText.Text = exception.Message;
            return;
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
            Contacts = ToInputs(_contacts, ProjectContactKind.Customer),
            ClientContacts = ToInputs(_clientContacts, ProjectContactKind.Client),
            Milestones = milestones,
            Description = DescriptionBox.Text,
            ResponsibleUserId = ResponsibleBox.SelectedValue is Guid responsibleId && responsibleId != Guid.Empty
                ? responsibleId
                : null,
            FolderPath = FolderBox.Text,
            ImagePath = ImageBox.Text,
            Status = status,
            CreateFolderIfMissing = CreateFolderCheck.IsChecked == true,
        };

        try
        {
            if (_projectId is null)
            {
                SavedProjectId = _services.Projects.Create(input).Id;
            }
            else
            {
                _services.Projects.Update(_projectId.Value, input);
                SavedProjectId = _projectId.Value;
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

    private static void CommitEdits(DataGrid grid)
    {
        try
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch (InvalidOperationException)
        {
            // Nichts in Bearbeitung: kein Grund, das Speichern abzubrechen.
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
