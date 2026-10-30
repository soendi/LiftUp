using System.Windows;
using LiftUp.Core.Domain;
using LiftUp.Core.Security;
using LiftUp.Core.Services;

namespace LiftUp.App;

public partial class ChangePasswordWindow : Window
{
    private readonly AuthService _auth;
    private readonly Session _session;

    public ChangePasswordWindow(AuthService auth, Session session, bool forced)
    {
        _auth = auth;
        _session = session;
        InitializeComponent();

        HeadingText.Text = forced ? "Neues Passwort vergeben" : "Passwort ändern";
        HintText.Text = forced
            ? "Das Startpasswort muss vor der ersten Nutzung geändert werden. Mindestens 8 Zeichen, Buchstaben und Ziffern."
            : $"Angemeldet als {session.UserName}. Mindestens 8 Zeichen, Buchstaben und Ziffern.";

        CancelButton.Visibility = forced ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => CurrentInput.Focus();
    }

    public Session? UpdatedSession { get; private set; }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        if (!string.Equals(NewInput.Password, RepeatInput.Password, StringComparison.Ordinal))
        {
            ErrorText.Text = "Die Wiederholung stimmt nicht mit dem neuen Passwort überein.";
            return;
        }

        try
        {
            PasswordPolicy.Validate(NewInput.Password);
            UpdatedSession = _auth.ChangePassword(_session, CurrentInput.Password, NewInput.Password);
            DialogResult = true;
        }
        catch (ValidationException exception)
        {
            ErrorText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            ErrorText.Text = "Das Passwort konnte nicht geändert werden: " + exception.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
