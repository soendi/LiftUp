using System.Net;
using System.Windows;
using System.Windows.Controls;
using LiftUp.App.Mail;
using LiftUp.Core.Domain;
using LiftUp.Core.Mail;

namespace LiftUp.App.Views;

/// <summary>
/// Verbindet ein Postfach per OAuth und versendet eine Nachricht mit dem
/// erzeugten PDF aus diesem Postfach.
/// </summary>
public partial class MailWindow : Window
{
    /// <summary>Ein Empfänger aus dem Projekt: Kunde oder Auftraggeber samt E-Mail.</summary>
    private sealed record RecipientChoice(string Display, string Address);

    private readonly AppServices _services;

    /// <summary>Projekt des Anhangs; daraus stammen die Empfänger zur Auswahl.</summary>
    private readonly Guid? _projectId;

    public MailWindow(
        AppServices services,
        string? attachmentPath = null,
        string? subject = null,
        Guid? projectId = null)
    {
        _services = services;
        _projectId = projectId;
        InitializeComponent();

        ProviderBox.ItemsSource = MailProviders.All;

        var account = _services.Mail.GetAccount();
        ProviderBox.SelectedItem = MailProviders.All.FirstOrDefault(info => info.Value == account.Provider);
        ClientIdBox.Text = account.ClientId;

        AttachmentBox.Text = attachmentPath ?? string.Empty;
        SubjectBox.Text = subject ?? string.Empty;

        LoadRecipients();
        UpdateAccountState();
    }

    /// <summary>
    /// Ansprechpersonen des Projekts mit E-Mail-Adresse – Kunde und Auftraggeber – als Auswahl
    /// für „An“ oder „Cc“. Ohne Projekt oder ohne Adressen bleibt die Auswahl verborgen.
    /// </summary>
    private void LoadRecipients()
    {
        if (_projectId is not { } projectId)
        {
            RecipientPanel.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var project = _services.Projects.GetProject(projectId);
            var choices = new List<RecipientChoice>();

            AddChoices(choices, project.Contacts, "Kunde");
            AddChoices(choices, project.ClientContacts, "Auftraggeber");

            ContactBox.ItemsSource = choices;
            ContactBox.SelectedIndex = choices.Count > 0 ? 0 : -1;
            RecipientPanel.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (choices.Count == 0)
            {
                InfoText.Text = "Für dieses Projekt sind keine E-Mail-Adressen erfasst.";
            }
        }
        catch (LiftUpException exception)
        {
            RecipientPanel.Visibility = Visibility.Collapsed;
            InfoText.Text = exception.Message;
        }
    }

    /// <summary>Übernimmt die Personen einer Liste, die eine E-Mail-Adresse haben.</summary>
    private static void AddChoices(
        List<RecipientChoice> choices,
        IEnumerable<ProjectContact> contacts,
        string kind)
    {
        foreach (var contact in contacts)
        {
            var email = contact.Email?.Trim() ?? string.Empty;
            if (email.Length == 0)
            {
                continue;
            }

            var name = contact.Name?.Trim() ?? string.Empty;
            var address = name.Length == 0 ? email : $"{name} <{email}>";

            // Dieselbe Adresse nicht doppelt anbieten.
            if (choices.Any(choice => string.Equals(choice.Address, address, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var display = name.Length == 0 ? $"{kind} · {email}" : $"{kind} · {name} ({email})";
            choices.Add(new RecipientChoice(display, address));
        }
    }

    private void OnAddToClick(object sender, RoutedEventArgs e) => AddRecipient(ToBox);

    private void OnAddCcClick(object sender, RoutedEventArgs e) => AddRecipient(CcBox);

    /// <summary>Hängt die gewählte Adresse an ein Feld; mehrere Adressen trennt ein Semikolon.</summary>
    private void AddRecipient(TextBox box)
    {
        if (ContactBox.SelectedItem is not RecipientChoice choice)
        {
            InfoText.Text = "Bitte zuerst eine Person aus dem Projekt wählen.";
            return;
        }

        var current = box.Text.Trim();
        if (current.Contains(choice.Address, StringComparison.OrdinalIgnoreCase))
        {
            InfoText.Text = $"{choice.Address} steht bereits im Feld.";
            return;
        }

        box.Text = current.Length == 0 ? choice.Address : $"{current}; {choice.Address}";
        box.CaretIndex = box.Text.Length;
        InfoText.Text = $"Empfänger übernommen: {choice.Address}";
    }

    private void UpdateAccountState()
    {
        var account = _services.Mail.GetAccount();
        var connected = _services.Mail.IsConnected;

        AccountText.Text = connected
            ? string.IsNullOrWhiteSpace(account.EmailAddress)
                ? $"Verbunden mit {MailProviders.Get(account.Provider).Name}."
                : $"Verbunden mit {MailProviders.Get(account.Provider).Name} als {account.EmailAddress}."
            : "Noch kein Postfach verbunden. Bitte oben die Zugangsdaten eintragen und anmelden.";

        LoginButton.Content = connected ? "Neu anmelden" : "Anmelden";
        DisconnectButton.IsEnabled = connected;
        SendButton.IsEnabled = connected;
    }

    private MailProvider SelectedProvider =>
        (ProviderBox.SelectedItem as MailProviderInfo)?.Value ?? MailProvider.Google;

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            var clientId = ClientIdBox.Text.Trim();
            if (clientId.Length == 0)
            {
                return;
            }

            _services.Mail.SaveAccount(new MailAccount { Provider = SelectedProvider, ClientId = clientId });
            UpdateAccountState();
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        var clientId = ClientIdBox.Text.Trim();
        if (clientId.Length == 0)
        {
            InfoText.Text = "Bitte zuerst die Client-ID der eigenen App-Registrierung eintragen.";
            return;
        }

        LoginButton.IsEnabled = false;
        InfoText.Text = "Der Browser wurde geöffnet. Bitte die Anmeldung dort abschließen …";

        try
        {
            var account = new MailAccount
            {
                Provider = SelectedProvider,
                ClientId = clientId,
            };

            var connected = await MailOAuthLogin.ConnectAsync(_services, account);
            ClientIdBox.Text = connected.ClientId;
            InfoText.Text = string.IsNullOrWhiteSpace(connected.EmailAddress)
                ? "Das Postfach ist verbunden."
                : $"Verbunden als {connected.EmailAddress}.";
            UpdateAccountState();
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Anmeldung fehlgeschlagen: " + exception.Message;
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        _services.Mail.Disconnect();
        InfoText.Text = "Die Verbindung wurde getrennt.";
        UpdateAccountState();
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        var body = BodyBox.Text ?? string.Empty;
        var message = new MailMessage
        {
            To = ToBox.Text.Trim(),
            Cc = string.IsNullOrWhiteSpace(CcBox.Text) ? null : CcBox.Text.Trim(),
            Subject = SubjectBox.Text.Trim(),
            BodyHtml = "<p>" + WebUtility.HtmlEncode(body).Replace("\r\n", "\n").Replace("\n", "<br />") + "</p>",
            AttachmentPath = string.IsNullOrWhiteSpace(AttachmentBox.Text) ? null : AttachmentBox.Text,
        };

        SendButton.IsEnabled = false;
        InfoText.Text = "Die Nachricht wird gesendet …";

        try
        {
            await _services.Mail.SendAsync(message);
            InfoText.Text = "Die Nachricht wurde gesendet.";
            DialogResult = true;
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Versand fehlgeschlagen: " + exception.Message;
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
