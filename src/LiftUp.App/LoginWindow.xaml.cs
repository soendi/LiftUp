using System.Windows;
using LiftUp.Core.Domain;
using LiftUp.Core.Security;

namespace LiftUp.App;

public partial class LoginWindow : Window
{
    private readonly AppServices _services;

    public LoginWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        FooterText.Text =
            $"Erstanmeldung: Benutzer \"admin\", Passwort \"admin\" – das Passwort wird beim ersten Login geändert.\n" +
            $"Datenbank: {_services.Database.FilePath}";

        Loaded += (_, _) => UserNameBox.Focus();
    }

    private void OnLoginClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        try
        {
            var result = _services.Auth.Login(UserNameBox.Text, PasswordInput.Password);
            if (!result.Success || result.Session is null)
            {
                ErrorText.Text = result.Error ?? "Anmeldung fehlgeschlagen.";
                PasswordInput.Clear();
                PasswordInput.Focus();
                return;
            }

            var session = result.Session;
            if (session.MustChangePassword)
            {
                var change = new ChangePasswordWindow(_services.Auth, session, forced: true) { Owner = this };
                if (change.ShowDialog() != true)
                {
                    ErrorText.Text = "Ohne neues Passwort ist keine Anmeldung möglich.";
                    return;
                }

                session = change.UpdatedSession ?? session;
            }

            _services.SetSession(session);
            DialogResult = true;
        }
        catch (LiftUpException exception)
        {
            ErrorText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            ErrorText.Text = "Unerwarteter Fehler bei der Anmeldung: " + exception.Message;
        }
    }
}
