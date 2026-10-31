using System.Windows;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>Pflegt die eigenen Absenderdaten für Titelseiten.</summary>
public partial class CompanyProfileWindow : Window
{
    private readonly AppServices _services;

    public CompanyProfileWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var profile = _services.Company.Get();
        NameBox.Text = profile.Name;
        ContactBox.Text = profile.Contact;
        EmailBox.Text = profile.Email;
        PhoneBox.Text = profile.Phone;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _services.Company.Save(new CompanyProfile
            {
                Name = NameBox.Text,
                Contact = ContactBox.Text,
                Email = EmailBox.Text,
                Phone = PhoneBox.Text,
            });

            DialogResult = true;
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Speichern fehlgeschlagen: " + exception.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
