using System.Windows;
using System.Windows.Controls;
using LiftUp.App.Views;
using LiftUp.Core.Domain;
using LiftUp.Core.Updates;

namespace LiftUp.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private bool _updateRunning;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var session = _services.CurrentSession;
        UserText.Text = $"{session.DisplayName} · {session.RoleName}";

        if (session.Can(Permission.ViewProjects))
        {
            Tabs.Items.Add(new TabItem { Header = "Projekte", Content = new ProjectsView(_services) });
        }

        if (session.Can(Permission.CreateDocuments) || session.Can(Permission.ManageTemplates))
        {
            Tabs.Items.Add(new TabItem { Header = "PDF", Content = new PdfView(_services) });
        }

        if (session.Can(Permission.ManageTemplates))
        {
            Tabs.Items.Add(new TabItem { Header = "Vorlagen", Content = new TemplatesView(_services) });
        }

        if (session.Can(Permission.ManageImages) || session.Can(Permission.CreateDocuments))
        {
            Tabs.Items.Add(new TabItem { Header = "Bilder", Content = new ImagesView(_services) });
        }

        if (session.Can(Permission.ManageSnippets) || session.Can(Permission.ViewProjects))
        {
            Tabs.Items.Add(new TabItem { Header = "Textbausteine", Content = new SnippetsView(_services) });
        }

        if (session.Can(Permission.ManageUsers))
        {
            Tabs.Items.Add(new TabItem { Header = "Benutzer", Content = new UsersView(_services) });
            Tabs.Items.Add(new TabItem { Header = "Sicherung", Content = new BackupsView(_services) });
        }

        if (session.Can(Permission.ManageRoles))
        {
            Tabs.Items.Add(new TabItem { Header = "Rollen und Rechte", Content = new RolesView(_services) });
        }

        StatusText.Text = $"Angemeldet als {session.UserName} · Datenbank: {_services.Database.FilePath}";
        SyncText.Text = _services.Sync.IsConfigured
            ? "Synchronisation: verbunden"
            : "Synchronisation: lokal (PostgreSQL später)";
        UpdateText.Text = $"Version {_services.Updates.CurrentVersion}";

        if (Tabs.Items.Count == 0)
        {
            Tabs.Items.Add(new TabItem
            {
                Header = "Keine Rechte",
                Content = new TextBlock
                {
                    Text = "Dieser Benutzer hat keine Rechte. Bitte an einen Administrator wenden.",
                    Margin = new Thickness(16),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        // Stille Update-Prüfung beim Start; abschaltbar über LIFTUP_DISABLE_AUTOUPDATE=1.
        Loaded += async (_, _) =>
        {
            if (UpdateService.AutoUpdateAllowed)
            {
                await RunUpdateAsync(userInitiated: false);
            }
        };
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) =>
        await RunUpdateAsync(userInitiated: true);

    private async Task RunUpdateAsync(bool userInitiated)
    {
        if (_updateRunning)
        {
            return;
        }

        _updateRunning = true;
        try
        {
            UpdateText.Text = "Prüfe auf Updates …";
            var result = await _services.Updates.CheckAsync();

            if (!result.UpdateAvailable || result.Package is null)
            {
                UpdateText.Text = userInitiated
                    ? result.Message
                    : $"Version {_services.Updates.CurrentVersion}";
                return;
            }

            var package = result.Package;
            var store = new UpdateAttemptStore();

            if (!userInitiated && !store.ShouldAttempt(package.Version))
            {
                UpdateText.Text = $"Update {package.Version} wartet auf den nächsten Start";
                return;
            }

            if (userInitiated)
            {
                var answer = MessageBox.Show(
                    $"Version {package.Version} ist verfügbar (installiert: {_services.Updates.CurrentVersion}).\n\n" +
                    "Jetzt installieren? LiftUp wird dafür beendet und anschließend neu gestartet.",
                    "Update installieren", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    UpdateText.Text = "Update abgebrochen";
                    return;
                }
            }

            var progress = new Progress<double>(value =>
                UpdateText.Text = $"Update {package.Version} wird geladen … {value:P0}");
            var installerPath = await _services.Updates.DownloadInstallerAsync(package, progress);

            store.Record(package.Version);
            UpdateText.Text = $"Update {package.Version} wird installiert …";

            if (!UpdateService.LaunchSilentInstall(installerPath))
            {
                UpdateText.Text = "Installation konnte nicht gestartet werden";
                MessageBox.Show(
                    $"Das Installationspaket liegt unter:\n{installerPath}\n\nBitte manuell ausführen.",
                    "Update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            UpdateText.Text = "Update fehlgeschlagen";
            if (userInitiated)
            {
                MessageBox.Show(exception.Message, "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _updateRunning = false;
        }
    }

    private void OnChangePasswordClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ChangePasswordWindow(_services.Auth, _services.CurrentSession, forced: false)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() == true && dialog.UpdatedSession is not null)
        {
            _services.SetSession(dialog.UpdatedSession);
            MessageBox.Show("Das Passwort wurde geändert.", "LiftUp", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        var login = new LoginWindow(_services);
        if (login.ShowDialog() != true)
        {
            return;
        }

        var next = new MainWindow(_services);
        System.Windows.Application.Current.MainWindow = next;
        next.Show();
        Close();
    }
}
