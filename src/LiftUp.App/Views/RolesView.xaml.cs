using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class RolesView : UserControl
{
    private readonly AppServices _services;
    private readonly List<CheckBox> _permissionBoxes = [];

    private Guid? _currentRoleId;
    private bool _isSystemRole;

    public RolesView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        foreach (var info in PermissionCatalog.All)
        {
            var box = new CheckBox
            {
                Content = info.Name,
                ToolTip = info.Description,
                Tag = info.Value,
                Margin = new Thickness(0, 3, 0, 3),
            };
            _permissionBoxes.Add(box);
            PermissionPanel.Children.Add(box);
        }

        LoadRoles();
    }

    private void LoadRoles(Guid? selectRoleId = null)
    {
        try
        {
            var roles = _services.Roles.GetAll();
            RoleList.ItemsSource = roles;

            var selection = selectRoleId is null
                ? null
                : roles.FirstOrDefault(role => role.Id == selectRoleId);
            RoleList.SelectedItem = selection ?? roles.FirstOrDefault();
        }
        catch (LiftUpException exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadRoles(_currentRoleId);

    private void OnRoleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RoleList.SelectedItem is not Role role)
        {
            return;
        }

        _currentRoleId = role.Id;
        _isSystemRole = role.IsSystem;

        NameBox.Text = role.Name;
        DescriptionBox.Text = role.Description;
        NameBox.IsReadOnly = role.IsSystem;
        DeleteButton.IsEnabled = !role.IsSystem;
        HeadingText.Text = role.IsSystem ? $"Rolle: {role.Name} (Systemrolle)" : $"Rolle: {role.Name}";
        SystemHintText.Text = role.IsSystem
            ? "Systemrollen können nicht gelöscht oder umbenannt werden. Die Rechte lassen sich anpassen; die Rolle \"Administrator\" behält immer alle Rechte."
            : string.Empty;

        var isAdministrator = string.Equals(role.Name, "Administrator", StringComparison.OrdinalIgnoreCase);
        foreach (var box in _permissionBoxes)
        {
            var permission = (Permission)box.Tag!;
            var isChecked = role.Permissions.HasFlag(permission);
            box.IsChecked = isAdministrator || isChecked;
            box.IsEnabled = !isAdministrator;
        }

        ErrorText.Text = string.Empty;
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        _currentRoleId = null;
        _isSystemRole = false;

        RoleList.SelectedItem = null;
        HeadingText.Text = "Neue Rolle anlegen";
        NameBox.Text = string.Empty;
        NameBox.IsReadOnly = false;
        DescriptionBox.Text = string.Empty;
        DeleteButton.IsEnabled = false;
        SystemHintText.Text = string.Empty;
        ErrorText.Text = string.Empty;

        foreach (var box in _permissionBoxes)
        {
            box.IsChecked = false;
            box.IsEnabled = true;
        }

        NameBox.Focus();
    }

    private Permission ReadPermissions()
    {
        var permissions = Permission.None;
        foreach (var box in _permissionBoxes)
        {
            if (box.IsChecked == true)
            {
                permissions |= (Permission)box.Tag!;
            }
        }

        return permissions;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        var permissions = ReadPermissions();

        try
        {
            if (_currentRoleId is null)
            {
                var role = _services.Roles.Create(NameBox.Text, DescriptionBox.Text, permissions);
                LoadRoles(role.Id);
            }
            else
            {
                var role = _services.Roles.Update(_currentRoleId.Value, NameBox.Text, DescriptionBox.Text, permissions);
                LoadRoles(role.Id);
            }

            if (_currentRoleId is not null && _currentRoleId == _services.CurrentSession.RoleId)
            {
                MessageBox.Show(
                    "Sie haben Rechte Ihrer eigenen Rolle geändert. Bitte melden Sie sich neu an, damit die Änderung greift.",
                    "LiftUp", MessageBoxButton.OK, MessageBoxImage.Information);
            }
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
            ErrorText.Text = "Die Rolle konnte nicht gespeichert werden: " + exception.Message;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_currentRoleId is null || _isSystemRole)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Rolle \"{NameBox.Text}\" wirklich löschen?",
            "Rolle löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Roles.Delete(_currentRoleId.Value);
            LoadRoles();
        }
        catch (LiftUpException exception)
        {
            ErrorText.Text = exception.Message;
        }
    }
}
