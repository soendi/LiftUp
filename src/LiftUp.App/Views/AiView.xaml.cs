using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using LiftUp.Core.Ai;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;

namespace LiftUp.App.Views;

/// <summary>
/// Reiter „KI“: Anbieter wählen, Anmeldung hinterlegen und die Verbindung prüfen. Die
/// Zugangsdaten liegen geschützt in der Datenbank; jedes Textfeld der Anwendung bietet
/// danach die vier Überarbeitungen über dem Editor an.
/// </summary>
public partial class AiView : UserControl
{
    private readonly AppServices _services;
    private bool _loading;
    private bool _busy;

    public AiView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        ProviderBox.ItemsSource = AiProviders.All;
        AuthBox.ItemsSource = new[] { "API-Schlüssel", "OAuth2-Zugangstoken" };
        Load();
    }

    private AiProvider SelectedProvider => ProviderBox.SelectedItem as AiProvider ?? AiProviders.All[0];

    /// <summary>Füllt die Felder mit der gespeicherten Verbindung.</summary>
    private void Load()
    {
        _loading = true;
        try
        {
            var connection = _services.Ai.Load();
            ProviderBox.SelectedItem = connection.Provider;
            AuthBox.SelectedIndex = connection.AuthMode == AiAuthMode.Token ? 1 : 0;
            SecretBox.Password = connection.Secret;
            BaseUrlBox.Text = connection.BaseUrl;
            ModelBox.ItemsSource = connection.Provider.Models;
            ModelBox.Text = connection.Model;
            UpdateProviderInfo(connection.Provider);
            UpdateState(connection);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Hinweis zum Schlüssel und Adresse des Anbieters nachziehen.</summary>
    private void UpdateProviderInfo(AiProvider provider)
    {
        KeyHintText.Text = provider.NeedsKey
            ? provider.KeyHint
            : "Ein lokales Modell braucht keinen Schlüssel.";
        SetupLink.NavigateUri = new Uri(provider.SetupUrl);
        SetupLinkText.Text = provider.SetupUrl;
    }

    private void UpdateState(AiConnection connection)
    {
        if (!connection.IsConfigured)
        {
            StateText.Text = "Keine KI verbunden – bitte Anbieter wählen und den Schlüssel hinterlegen.";
            return;
        }

        var auth = connection.AuthMode == AiAuthMode.Token ? "OAuth2-Zugangstoken" : "API-Schlüssel";
        StateText.Text = $"Verbunden mit {connection.Provider.Name} · Modell {connection.Model} · {auth}.";
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var provider = SelectedProvider;
        ModelBox.ItemsSource = provider.Models;
        ModelBox.Text = provider.DefaultModel;
        BaseUrlBox.Text = provider.BaseUrl;
        UpdateProviderInfo(provider);
        InfoText.Text = "Anbieter gewechselt – bitte Schlüssel eintragen und speichern.";
    }

    private void OnAuthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SecretLabel.Text = AuthBox.SelectedIndex == 1 ? "Zugangstoken" : "API-Schlüssel";
    }

    /// <summary>Baut die Verbindung aus den Feldern – ohne sie zu speichern.</summary>
    private AiConnection BuildConnection() =>
        new(
            SelectedProvider,
            ModelBox.Text,
            BaseUrlBox.Text,
            AuthBox.SelectedIndex == 1 ? AiAuthMode.Token : AiAuthMode.ApiKey,
            SecretBox.Password);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var connection = BuildConnection();
        try
        {
            _services.Ai.Save(connection);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
            return;
        }

        UpdateState(connection);
        InfoText.Text = connection.IsConfigured
            ? "Gespeichert. Die KI steht jetzt über jedem Text-Editor zur Verfügung."
            : "Gespeichert – ohne Schlüssel bleibt die KI jedoch ungenutzt.";
    }

    /// <summary>Prüft die eingetragenen Daten mit einer kleinen Anfrage.</summary>
    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var connection = BuildConnection();
        if (!connection.IsConfigured)
        {
            InfoText.Text = "Bitte zuerst Anbieter, Modell und Schlüssel eintragen.";
            return;
        }

        _busy = true;
        TestButton.IsEnabled = false;
        InfoText.Text = $"Verbindung zu {connection.Provider.Name} wird geprüft …";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var answer = await _services.Ai.TestAsync(connection, timeout.Token);

            _services.Ai.Save(connection);
            UpdateState(connection);
            InfoText.Text = $"Verbindung steht: {connection.Provider.Name} antwortet mit „{answer}“. "
                            + "Die Daten wurden gespeichert.";
        }
        catch (Exception exception) when (exception is LiftUpException or OperationCanceledException or InvalidOperationException)
        {
            InfoText.Text = exception is OperationCanceledException
                ? "Der Anbieter hat nicht innerhalb einer Minute geantwortet."
                : exception.Message;
        }
        finally
        {
            _busy = false;
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>Entfernt den gespeicherten Schlüssel; die Felder bleiben zum Neu-Eintragen stehen.</summary>
    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        var connection = BuildConnection() with { Secret = string.Empty };
        _services.Ai.Save(connection);
        SecretBox.Password = string.Empty;
        UpdateState(connection);
        InfoText.Text = "Der gespeicherte Schlüssel wurde entfernt.";
    }

    private void OnOpenSetupClick(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            InfoText.Text = "Der Browser konnte nicht geöffnet werden. Adresse: " + e.Uri.AbsoluteUri;
        }

        e.Handled = true;
    }
}
