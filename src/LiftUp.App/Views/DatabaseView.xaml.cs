using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;
using LiftUp.Core.Sync;
using Microsoft.Win32;

namespace LiftUp.App.Views;

/// <summary>
/// Reiter „Datenbank“: Zugangsdaten der zentralen PostgreSQL-Datenbank eintragen,
/// die vom Server-Setup erzeugte Datei einlesen und den Abgleich auslösen.
/// </summary>
public partial class DatabaseView : UserControl
{
    private readonly AppServices _services;
    private readonly Session _session;
    private bool _busy;

    public DatabaseView(AppServices services)
    {
        _services = services;
        _session = services.CurrentSession;
        InitializeComponent();

        SslBox.ItemsSource = DatabaseServerSettings.SslModes;
        SslBox.SelectedItem = "Prefer";

        Loaded += (_, _) => LoadSettings();
        _services.Sync.ConfigurationChanged += OnConfigurationChanged;
        Unloaded += (_, _) => _services.Sync.ConfigurationChanged -= OnConfigurationChanged;
    }

    private void OnConfigurationChanged(object? sender, EventArgs e) => RefreshStatus();

    private void LoadSettings()
    {
        var settings = new SyncSettingsStore(_services.Database).Load()
                       ?? new DatabaseServerSettings();

        HostBox.Text = settings.Host;
        PortBox.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        DatabaseNameBox.Text = settings.Database;
        UserBox.Text = settings.User;
        PasswordBox.Password = settings.Password;
        SslBox.SelectedItem = DatabaseServerSettings.SslModes
            .FirstOrDefault(mode => string.Equals(mode, settings.SslMode, StringComparison.OrdinalIgnoreCase))
            ?? "Prefer";

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var store = new SyncSettingsStore(_services.Database);
        var configured = _services.Sync.IsConfigured;

        StateText.Text = configured
            ? $"Eingerichtet: {_services.Sync.TransportName}"
            : "Nicht eingerichtet – LiftUp arbeitet nur mit der lokalen Datenbank.";
        PendingText.Text = $"Offene Änderungen: {_services.Sync.PendingCount}";
        LastRunText.Text = store.LastRunUtc is { } lastRun
            ? $"Letzter Abgleich: {lastRun.ToLocalTime():dd.MM.yyyy HH:mm} – {store.LastMessage}"
            : "Noch kein Abgleich gelaufen.";
        DeviceText.Text = $"Gerätekennung dieses Arbeitsplatzes: {_services.Database.GetDeviceId()}";
    }

    /// <summary>Liest die Eingaben und prüft sie; bei Fehlern bleibt das Formular stehen.</summary>
    private DatabaseServerSettings? ReadForm()
    {
        if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            port = 0;
        }

        var settings = new DatabaseServerSettings
        {
            Host = HostBox.Text.Trim(),
            Port = port,
            Database = DatabaseNameBox.Text.Trim(),
            User = UserBox.Text.Trim(),
            Password = PasswordBox.Password,
            SslMode = SslBox.SelectedItem as string ?? "Prefer",
        };

        var errors = settings.Validate();
        if (errors.Count == 0)
        {
            return settings;
        }

        MessageBox.Show(string.Join(Environment.NewLine, errors), "Datenbank",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Verbindungsdatei wählen",
            Filter = "LiftUp-Verbindungsdatei (*.json)|*.json|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var file = ConnectionFile.Load(dialog.FileName);
            var settings = file.ToSettings();

            HostBox.Text = settings.Host;
            PortBox.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
            DatabaseNameBox.Text = settings.Database;
            UserBox.Text = settings.User;
            PasswordBox.Password = settings.Password;
            SslBox.SelectedItem = DatabaseServerSettings.SslModes
                .FirstOrDefault(mode => string.Equals(mode, settings.SslMode, StringComparison.OrdinalIgnoreCase))
                ?? "Prefer";

            InfoText.Text = string.IsNullOrWhiteSpace(file.ServerVersion)
                ? "Datei gelesen. Bitte die Angaben prüfen und speichern."
                : $"Datei gelesen (PostgreSQL {file.ServerVersion}). Bitte die Angaben prüfen und speichern.";
        }
        catch (ValidationException exception)
        {
            InfoText.Text = exception.Message;
            MessageBox.Show(exception.Message, "Datei einlesen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var settings = ReadForm();
        if (settings is null)
        {
            return;
        }

        _busy = true;
        InfoText.Text = "Verbinde …";
        try
        {
            var message = await PostgresSyncTransport.TestAsync(settings);
            InfoText.Text = message;
        }
        catch (Exception exception)
        {
            InfoText.Text = $"Keine Verbindung: {exception.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_session.Can(Permission.ChangeSettings))
        {
            MessageBox.Show("Für das Ändern der Datenbankverbindung fehlt das Recht „Einstellungen ändern“.",
                "Datenbank", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = ReadForm();
        if (settings is null)
        {
            return;
        }

        new SyncSettingsStore(_services.Database).Save(settings);
        _services.Sync.Reload();
        InfoText.Text = settings.IsComplete
            ? $"Gespeichert. LiftUp gleicht mit {settings.Describe()} ab."
            : "Gespeichert. Ohne vollständige Angaben bleibt LiftUp lokal.";
        RefreshStatus();
    }

    private async void OnSyncClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!_session.Can(Permission.RunSync))
        {
            MessageBox.Show("Für den Abgleich fehlt das Recht „Synchronisierung ausführen“.",
                "Datenbank", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _busy = true;
        InfoText.Text = "Abgleich läuft …";
        try
        {
            var result = await _services.Sync.RunAsync();
            InfoText.Text = result.Message;
        }
        finally
        {
            _busy = false;
            RefreshStatus();
        }
    }
}
