using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;
using LiftUp.Core.Security;
using LiftUp.Core.Services;

namespace LiftUp.App.Views;

/// <summary>
/// Notfall-Administration: Passwörter zurücksetzen, Konten entsperren und – falls nötig –
/// einen neuen Administrator anlegen. Alle Aktionen laufen über die Core-Dienste und damit
/// über die zentrale Rechteprüfung; gelöscht wird hier nichts.
/// </summary>
public partial class EmergencyAdminView : UserControl
{
    private readonly AppServices _services;

    public EmergencyAdminView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        TimeoutBox.Text = _services.Emergency.TimeoutMinutes.ToString();
        UpdateSessionText();
        LoadUsers();
    }

    /// <summary>Wird ausgelöst, wenn der Notfallzugang beendet und abgemeldet werden soll.</summary>
    public event EventHandler? EndRequested;

    private Session Session => _services.CurrentSession;

    private UserListItem? Selected => UsersGrid.SelectedItem as UserListItem;

    public void UpdateSessionText()
    {
        var manager = _services.EmergencySession;
        SessionText.Text = manager is null
            ? $"Notfallzugang als {Session.UserName}."
            : $"Notfallzugang als {Session.UserName} · endet ohne Bedienung um "
              + $"{manager.ExpiresUtc.ToLocalTime():HH:mm} Uhr.";
    }

    private void LoadUsers()
    {
        try
        {
            var items = _services.Users.GetUsers();
            UsersGrid.ItemsSource = items;
            StatusText.Text = items.Count == 1 ? "1 Benutzer" : $"{items.Count} Benutzer";
        }
        catch (LiftUpException exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void OnResetPasswordClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            StatusText.Text = "Bitte zuerst einen Benutzer auswählen.";
            return;
        }

        if (!string.Equals(NewPasswordBox.Password, RepeatPasswordBox.Password, StringComparison.Ordinal))
        {
            StatusText.Text = "Die Wiederholung stimmt nicht mit dem neuen Passwort überein.";
            return;
        }

        try
        {
            PasswordPolicy.Validate(NewPasswordBox.Password);
            _services.Users.ResetPassword(selected.Id, NewPasswordBox.Password, mustChangePassword: false);
            NewPasswordBox.Clear();
            RepeatPasswordBox.Clear();
            StatusText.Text =
                $"Das Passwort von \"{selected.UserName}\" wurde neu gesetzt. Die Anmeldung ist wieder möglich.";
            LoadUsers();
        }
        catch (LiftUpException exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            StatusText.Text = "Bitte zuerst einen Benutzer auswählen.";
            return;
        }

        try
        {
            _services.Users.Unlock(selected.Id);
            StatusText.Text = $"Das Konto \"{selected.UserName}\" ist wieder freigegeben.";
            LoadUsers();
        }
        catch (LiftUpException exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void OnCreateAdminClick(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(NewAdminPasswordBox.Password, NewAdminRepeatBox.Password, StringComparison.Ordinal))
        {
            StatusText.Text = "Die Wiederholung stimmt nicht mit dem neuen Passwort überein.";
            return;
        }

        try
        {
            PasswordPolicy.Validate(NewAdminPasswordBox.Password);

            var role = _services.Roles.GetAll().FirstOrDefault(item => item.Has(Permission.ManageUsers))
                       ?? throw new ValidationException(
                           "Es gibt keine Rolle mit Benutzerverwaltung. Bitte zuerst eine Rolle anlegen.");

            var created = _services.Users.Create(
                NewAdminNameBox.Text,
                NewAdminNameBox.Text,
                role.Id,
                NewAdminPasswordBox.Password,
                mustChangePassword: true);

            NewAdminNameBox.Clear();
            NewAdminPasswordBox.Clear();
            NewAdminRepeatBox.Clear();
            StatusText.Text =
                $"Der Administrator \"{created.UserName}\" wurde angelegt. Das Passwort muss bei der ersten Anmeldung geändert werden.";
            LoadUsers();
        }
        catch (LiftUpException exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void OnTimeoutChanged(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TimeoutBox.Text, out var minutes))
        {
            TimeoutBox.Text = _services.Emergency.TimeoutMinutes.ToString();
            return;
        }

        var applied = _services.Emergency.SetTimeoutMinutes(minutes);
        TimeoutBox.Text = applied.ToString();
        UpdateSessionText();
    }

    private void OnEndClick(object sender, RoutedEventArgs e) => EndRequested?.Invoke(this, EventArgs.Empty);
}
