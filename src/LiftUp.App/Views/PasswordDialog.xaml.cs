using System.Windows;
using LiftUp.Core.Domain;
using LiftUp.Core.Security;

namespace LiftUp.App.Views;

public partial class PasswordDialog : Window
{
    public PasswordDialog(string userName)
    {
        InitializeComponent();
        HeadingText.Text = $"Neues Passwort für \"{userName}\"";
        Loaded += (_, _) => NewInput.Focus();
    }

    public string NewPassword { get; private set; } = string.Empty;
    public bool MustChangePassword { get; private set; } = true;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        if (!string.Equals(NewInput.Password, RepeatInput.Password, StringComparison.Ordinal))
        {
            ErrorText.Text = "Die Wiederholung stimmt nicht mit dem Passwort überein.";
            return;
        }

        try
        {
            PasswordPolicy.Validate(NewInput.Password);
        }
        catch (ValidationException exception)
        {
            ErrorText.Text = exception.Message;
            return;
        }

        NewPassword = NewInput.Password;
        MustChangePassword = MustChangeCheck.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
