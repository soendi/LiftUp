using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Data;
using LiftUp.Core.Diagnostics;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>
/// Reiter „Datenbank“: liest die vom Server-Installer erzeugte Verbindungsdatei ein,
/// merkt sie sich in der lokalen Datenbank, prüft die Verbindung und startet den
/// Abgleich des Änderungsprotokolls mit dem PostgreSQL-Server.
/// </summary>
public partial class DatabaseView : UserControl
{
    private readonly AppServices _services;
    private ServerProfile? _profile;
    private bool _checkRunning;
    private bool _syncRunning;

    public DatabaseView(AppServices services)
    {
        _services = services;
        InitializeComponent();
        LoadStored();
        UpdateSyncStatus();
    }

    private void LoadStored()
    {
        var source = _services.ServerProfiles.GetSourcePath();
        if (!string.IsNullOrWhiteSpace(source))
        {
            PathBox.Text = source;
        }

        ServerProfile? stored;
        try
        {
            stored = _services.ServerProfiles.Load();
        }
        catch (LiftUpException exception)
        {
            ShowEmpty();
            InfoText.Text = exception.Message;
            return;
        }

        if (stored is null)
        {
            ShowEmpty();
            return;
        }

        Apply(stored,
            "Die gespeicherten Einstellungen wurden geladen. Mit „Verbindung prüfen“ wird der Server getestet.");
    }

    private void Apply(ServerProfile profile, string message)
    {
        _profile = profile;

        ServerText.Text = profile.Endpoint;
        PortText.Text = profile.Port.ToString();
        DatabaseText.Text = profile.Database;
        SchemaText.Text = profile.Schema;
        UserText.Text = profile.UserName;
        SslText.Text = profile.SslMode;
        ServerVersionText.Text = string.IsNullOrWhiteSpace(profile.ServerVersion)
            ? "unbekannt"
            : profile.ServerVersion;

        var localVersion = Database.CurrentSchemaVersion;
        SchemaVersionText.Text = profile.SchemaVersion == localVersion
            ? $"{profile.SchemaVersion} (passt zur lokalen Datenbank)"
            : $"{profile.SchemaVersion} (lokal: {localVersion})";

        GeneratedText.Text = profile.GeneratedUtc == default
            ? "unbekannt"
            : profile.GeneratedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        SourceText.Text = _services.ServerProfiles.GetSourcePath() ?? "unbekannt";
        UpdatePassword();

        TablesHeading.Text = $"Tabellen im Schema „{profile.Schema}“ ({profile.Tables.Count})";
        TablesList.ItemsSource = profile.Tables;

        TestButton.IsEnabled = true;
        RemoveButton.IsEnabled = true;
        InfoText.Text = message;
        UpdateSyncState();
    }

    private void ShowEmpty()
    {
        _profile = null;

        ServerText.Text = "—";
        PortText.Text = "—";
        DatabaseText.Text = "—";
        SchemaText.Text = "—";
        UserText.Text = "—";
        SslText.Text = "—";
        ServerVersionText.Text = "—";
        SchemaVersionText.Text = "—";
        GeneratedText.Text = "—";
        SourceText.Text = "—";
        PasswordText.Text = "—";

        TablesHeading.Text = "Tabellen im Schema";
        TablesList.ItemsSource = null;

        TestButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;

        InfoText.Text =
            "Noch keine Verbindungsdatei eingelesen. Die Datei entsteht auf dem Datenbank-Server mit " +
            "Install-ServerDatabase.ps1" + DescribeBundledInstaller() + ".";

        UpdateSyncState();
    }

    /// <summary>
    /// Der Server-Installer liegt seit 1.0.14 mit im Programmordner. Ist er vorhanden,
    /// nennt der Hinweis seinen Pfad, damit man ihn auf den Datenbank-Server kopieren kann.
    /// </summary>
    private static string DescribeBundledInstaller()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "server", "Install-ServerDatabase.ps1");
        return File.Exists(path) ? $" (mitgeliefert unter {path})" : " (im Projektordner unter installer/)";
    }

    private void UpdatePassword()
    {
        if (_profile is null || string.IsNullOrEmpty(_profile.Password))
        {
            PasswordText.Text = "—";
            return;
        }

        PasswordText.Text = ShowPasswordCheck.IsChecked == true
            ? _profile.Password
            : new string('•', Math.Clamp(_profile.Password.Length, 8, 16));
    }

    private void OnShowPasswordChanged(object sender, RoutedEventArgs e) => UpdatePassword();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Verbindungsdatei wählen",
            Filter = "LiftUp-Verbindungsdatei (liftup-server.json)|liftup-server.json|JSON-Dateien (*.json)|*.json|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
        };

        try
        {
            var folder = Path.GetDirectoryName(PathBox.Text?.Trim() ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                dialog.InitialDirectory = folder;
            }
        }
        catch (ArgumentException)
        {
            // Ungültiger Pfad in der Eingabe: Dialog startet im Standardordner.
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            PathBox.Text = dialog.FileName;
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            InfoText.Text = "Bitte zuerst die Verbindungsdatei wählen.";
            return;
        }

        ServerProfile profile;
        try
        {
            profile = ServerProfile.Load(path);
            _services.ServerProfiles.Save(profile, path);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
            return;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Die Verbindungsdatei konnte nicht übernommen werden: " + exception.Message;
            return;
        }

        Apply(profile, $"Eingelesen: {Path.GetFileName(path)}.");
        UpdateSyncStatus();
        await CheckAsync(profile, $"Eingelesen: {Path.GetFileName(path)}.");
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_profile is null)
        {
            InfoText.Text = "Bitte zuerst eine Verbindungsdatei einlesen.";
            return;
        }

        await CheckAsync(_profile, $"Server {_profile.Endpoint}.");
    }

    private async Task CheckAsync(ServerProfile profile, string prefix)
    {
        if (_checkRunning)
        {
            return;
        }

        _checkRunning = true;
        TestButton.IsEnabled = false;

        try
        {
            InfoText.Text = prefix + " Prüfe Verbindung …";

            // Die lokalen Tabellen mitgeben, damit eine Schemaänderung der Anwendung
            // auffällt, die auf dem Server noch fehlt.
            var localTables = _services.Database.GetSchemaColumns().Keys;
            var result = await ServerDatabaseChecker.CheckAsync(profile, localTables);
            InfoText.Text = prefix + " " + result.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = prefix + " Prüfung fehlgeschlagen: " + exception.Message;
        }
        finally
        {
            _checkRunning = false;
            TestButton.IsEnabled = _profile is not null;
        }
    }

    /// <summary>Abgleich des Änderungsprotokolls mit dem eingelesenen Server.</summary>
    private async void OnSyncClick(object sender, RoutedEventArgs e)
    {
        if (_profile is null)
        {
            InfoText.Text = "Bitte zuerst eine Verbindungsdatei einlesen.";
            return;
        }

        if (_services.Session?.Can(Permission.RunSync) != true)
        {
            InfoText.Text = "Für den Abgleich fehlt das Recht „Synchronisierung ausführen“.";
            return;
        }

        _syncRunning = true;
        UpdateSyncState();
        SyncButton.Content = "Abgleich läuft …";

        try
        {
            var sync = _services.CreateSync();
            var result = await sync.RunAsync();

            SyncStatusText.Text = result.Message;
            InfoText.Text = result.Pending == 0
                ? "Abgleich abgeschlossen; alle Einträge sind beim Server angekommen."
                : $"Abgleich abgeschlossen; {result.Pending} Eintrag/Einträge bleiben offen.";
        }
        catch (Exception exception)
        {
            SyncStatusText.Text = "Abgleich fehlgeschlagen: " + exception.Message;
        }
        finally
        {
            _syncRunning = false;
            SyncButton.Content = "Jetzt abgleichen";
            UpdateSyncState();
        }
    }

    private void UpdateSyncState()
    {
        var allowed = _services.Session?.Can(Permission.RunSync) == true;
        SyncButton.IsEnabled = !_syncRunning && _profile is not null && allowed;
        SyncButton.ToolTip = allowed
            ? null
            : "Dafür fehlt das Recht „Synchronisierung ausführen“.";
    }

    private void UpdateSyncStatus()
    {
        var sync = _services.CreateSync();
        if (!sync.IsConfigured)
        {
            SyncStatusText.Text = "Kein Server eingelesen – der Abgleich ist nicht eingerichtet.";
            return;
        }

        var last = sync.GetLastRunUtc();
        var pending = sync.PendingCount;
        var lastText = last is null
            ? "noch nicht abgeglichen"
            : $"zuletzt {last.Value.ToLocalTime():dd.MM.yyyy HH:mm}";
        var pendingText = pending == 0 ? "keine offenen Einträge" : $"{pending} Eintrag/Einträge offen";

        SyncStatusText.Text = $"{lastText} · {pendingText}.";
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_profile is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            "Die gespeicherten Verbindungsangaben aus der lokalen Datenbank entfernen?\n\n" +
            "Die Datei auf dem Server bleibt unberührt und kann erneut eingelesen werden.",
            "Datenbank", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _services.ServerProfiles.Clear();
        PathBox.Text = string.Empty;
        ShowEmpty();
        UpdateSyncStatus();
    }
}
