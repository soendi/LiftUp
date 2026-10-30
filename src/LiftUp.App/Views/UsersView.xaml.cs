using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class UsersView : UserControl
{
    private readonly AppServices _services;

    public UsersView(AppServices services)
    {
        _services = services;
        InitializeComponent();
        LoadUsers();
    }

    private void LoadUsers()
    {
        try
        {
            var items = _services.Users.GetUsers(SearchBox.Text);
            UsersGrid.ItemsSource = items;
            InfoText.Text = items.Count == 1 ? "1 Benutzer" : $"{items.Count} Benutzer";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private UserListItem? Selected => UsersGrid.SelectedItem as UserListItem;

    private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadUsers();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => LoadUsers();

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        var dialog = new UserEditWindow(_services, null) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            LoadUsers();
        }
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            InfoText.Text = "Bitte zuerst einen Benutzer auswählen.";
            return;
        }

        var dialog = new UserEditWindow(_services, selected.Id) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (selected.Id == _services.CurrentSession.UserId)
        {
            MessageBox.Show(
                "Ihr eigenes Konto wurde geändert. Bitte melden Sie sich neu an, damit die geänderten Rechte greifen.",
                "LiftUp", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        LoadUsers();
    }

    private void OnResetPasswordClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            InfoText.Text = "Bitte zuerst einen Benutzer auswählen.";
            return;
        }

        var dialog = new PasswordDialog(selected.UserName) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _services.Users.ResetPassword(selected.Id, dialog.NewPassword, dialog.MustChangePassword);
            MessageBox.Show(
                $"Das Passwort für \"{selected.UserName}\" wurde gesetzt.",
                "LiftUp", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (LiftUpException exception)
        {
            MessageBox.Show(exception.Message, "Passwort zurücksetzen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            InfoText.Text = "Bitte zuerst einen Benutzer auswählen.";
            return;
        }

        var answer = MessageBox.Show(
            $"Konto \"{selected.UserName}\" wirklich löschen?",
            "Benutzer löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Users.Delete(selected.Id);
            LoadUsers();
        }
        catch (LiftUpException exception)
        {
            MessageBox.Show(exception.Message, "Benutzer löschen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null)
        {
            OnEditClick(sender, e);
        }
    }
}
