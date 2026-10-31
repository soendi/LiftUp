using System.Windows;
using LiftUp.Core.Domain;
using LiftUp.Core.Security;

namespace LiftUp.App.Views;

public partial class UserEditWindow : Window
{
    private readonly AppServices _services;
    private readonly Guid? _userId;

    public UserEditWindow(AppServices services, Guid? userId)
    {
        _services = services;
        _userId = userId;
        InitializeComponent();

        RoleBox.ItemsSource = _services.Roles.GetAll();

        if (userId is null)
        {
            HeadingText.Text = "Neues Konto anlegen";
            RoleBox.SelectedIndex = RoleBox.Items.Count > 0 ? RoleBox.Items.Count - 1 : -1;
            Loaded += (_, _) => UserNameBox.Focus();
        }
        else
        {
            HeadingText.Text = "Konto bearbeiten";
            PasswordPanel.Visibility = Visibility.Collapsed;
            HintText.Text = "Das Passwort wird über \"Passwort zurücksetzen\" geändert.";
            LoadUser(userId.Value);
        }
    }

    private void LoadUser(Guid userId)
    {
        try
        {
            var item = _services.Users.GetUser(userId);
            if (item is null)
            {
                ErrorText.Text = "Der Benutzer wurde nicht gefunden.";
                return;
            }

            UserNameBox.Text = item.UserName;
            UserNameBox.IsReadOnly = true;
            DisplayNameBox.Text = item.DisplayName;
            CompanyBox.Text = item.Company;
            EmailBox.Text = item.Email;
            PhoneBox.Text = item.Phone;
            ActiveCheck.IsChecked = item.IsActive;

            var roles = (IReadOnlyList<Role>)RoleBox.ItemsSource;
            RoleBox.SelectedItem = roles.FirstOrDefault(role => string.Equals(role.Name, item.RoleName, StringComparison.OrdinalIgnoreCase));
        }
        catch (LiftUpException exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        if (RoleBox.SelectedItem is not Role role)
        {
            ErrorText.Text = "Bitte eine Rolle auswählen.";
            return;
        }

        try
        {
            if (_userId is null)
            {
                if (!string.Equals(PasswordInput.Password, RepeatInput.Password, StringComparison.Ordinal))
                {
                    ErrorText.Text = "Die Wiederholung stimmt nicht mit dem Passwort überein.";
                    return;
                }

                PasswordPolicy.Validate(PasswordInput.Password);
                _services.Users.Create(
                    UserNameBox.Text,
                    DisplayNameBox.Text,
                    role.Id,
                    PasswordInput.Password,
                    MustChangeCheck.IsChecked == true,
                    CompanyBox.Text,
                    EmailBox.Text,
                    PhoneBox.Text);
            }
            else
            {
                _services.Users.Update(
                    _userId.Value,
                    DisplayNameBox.Text,
                    role.Id,
                    ActiveCheck.IsChecked == true,
                    CompanyBox.Text,
                    EmailBox.Text,
                    PhoneBox.Text);
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
            ErrorText.Text = "Der Benutzer konnte nicht gespeichert werden: " + exception.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
