using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiftUp.App.Views;
using LiftUp.Core.Domain;
using LiftUp.Core.Updates;

namespace LiftUp.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly AnalysisView? _analysisView;
    private readonly DispatcherTimer _syncTimer;
    private bool _updateRunning;
    private bool _syncRunning;
    private bool _closingAfterSync;
    private bool _skipSyncOnClose;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var session = _services.CurrentSession;
        UserText.Text = $"{session.DisplayName} · {session.RoleName}";

        // Die Reiter sind in Themengruppen geordnet: Übersicht, Inhalte, Verwaltung, System.
        // Zwischen den Gruppen steht je ein Trennstrich; leere Gruppen fallen weg.
        var groups = new List<List<TabItem>>();

        // Zustandsanalyse, Investitionsplanung, Analyse und PDF teilen die Projektauswahl.
        var selection = new ProjectSelection();

        // Die Haupt-Reiter tragen dieselben Farben wie ihr Inhalt: rot, solange nichts erfasst
        // ist, gelb wenn noch etwas fehlt oder ungespeichert ist, grün wenn alles da ist.
        var colored = new List<(TabItem Tab, string Header, ITabFillSource Source)>();

        var overview = new List<TabItem>();
        if (session.Can(Permission.ViewProjects))
        {
            overview.Add(CreateTab("Projekte", new ProjectsView(_services, selection)));
        }

        if (session.Can(Permission.ViewProjects))
        {
            // Die beiden Tabellen erscheinen direkt hinter den Projekten; die Zustandsanalyse
            // wird zusätzlich als zweites Blatt ins PDF übernommen.
            var stateView = new ProjectTableView(_services, ProjectTableCatalog.Zustandsanalyse, selection);
            var stateTab = CreateTab("Zustandsanalyse", stateView);
            overview.Add(stateTab);

            var investmentView = new ProjectTableView(_services, ProjectTableCatalog.Investitionsplanung, selection);
            var investmentTab = CreateTab("Investitionsplanung", investmentView);
            overview.Add(investmentTab);

            // Die Analyse wird je Projekt einmal erfasst und fließt in jedes Dokument ein.
            var analysisView = new AnalysisView(_services, selection);
            var analysisTab = CreateTab("Analyse", analysisView);
            overview.Add(analysisTab);

            // Beim Beenden werden offene Texte noch gespeichert.
            _analysisView = analysisView;

            colored.Add((stateTab, "Zustandsanalyse", stateView));
            colored.Add((investmentTab, "Investitionsplanung", investmentView));
            colored.Add((analysisTab, "Analyse", analysisView));
        }

        if (session.Can(Permission.CreateDocuments) || session.Can(Permission.ManageTemplates))
        {
            overview.Add(CreateTab("PDF", new PdfView(_services, selection)));
        }

        groups.Add(overview);

        var content = new List<TabItem>();
        if (session.Can(Permission.ManageTemplates))
        {
            content.Add(CreateTab("Vorlagen", new TemplatesView(_services)));
        }

        if (session.Can(Permission.ManageSnippets) || session.Can(Permission.ViewProjects))
        {
            content.Add(CreateTab("Textbausteine", new SnippetsView(_services)));
        }

        if (session.Can(Permission.ManageImages) || session.Can(Permission.CreateDocuments))
        {
            content.Add(CreateTab("Bilder", new ImagesView(_services, selection)));
        }

        groups.Add(content);

        var administration = new List<TabItem>();
        if (session.Can(Permission.ManageUsers))
        {
            administration.Add(CreateTab("Benutzer", new UsersView(_services)));
        }

        if (session.Can(Permission.ManageRoles))
        {
            administration.Add(CreateTab("Rollen und Rechte", new RolesView(_services)));
        }

        groups.Add(administration);

        var system = new List<TabItem>();
        if (session.Can(Permission.ChangeSettings))
        {
            system.Add(CreateTab("KI", new AiView(_services)));
            system.Add(CreateTab("Datenbank", new DatabaseView(_services)));
        }

        if (session.Can(Permission.ViewChangeLog))
        {
            system.Add(CreateTab("Protokoll", new ChangeLogView(_services)));
        }

        if (session.Can(Permission.ManageUsers))
        {
            system.Add(CreateTab("Sicherung", new BackupsView(_services)));
        }

        groups.Add(system);

        foreach (var group in groups)
        {
            if (group.Count == 0)
            {
                continue;
            }

            if (Tabs.Items.Count > 0)
            {
                Tabs.Items.Add(CreateSeparator());
            }

            foreach (var tab in group)
            {
                Tabs.Items.Add(tab);
            }
        }

        // Farbe der Haupt-Reiter: Sie folgt dem Inhalt und dem gewählten Projekt.
        foreach (var (tab, header, source) in colored)
        {
            source.FillChanged += (_, _) => TabHeaderStyle.Apply(tab, header, source.Fill);
            selection.Changed += (_, _) => TabHeaderStyle.Apply(tab, header, source.Fill);
            TabHeaderStyle.Apply(tab, header, source.Fill);
        }

        // Geöffnet ist immer der erste echte Reiter; Trennstriche sind nicht wählbar.
        Tabs.SelectedIndex = Tabs.Items.Count > 0 ? 0 : -1;

        StatusText.Text = $"Angemeldet als {session.UserName} · Datenbank: {_services.Database.FilePath}";
        UpdateSyncText();
        UpdateText.Text = $"Version {_services.Updates.CurrentVersion}";

        // Nach dem Speichern im Reiter „Datenbank“ sofort den neuen Stand anzeigen.
        _services.Sync.ConfigurationChanged += (_, _) => UpdateSyncText();

        // Abgleich beim Start, alle fünf Minuten und vor dem Schließen.
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _syncTimer.Tick += async (_, _) => await TrySyncAsync();

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
            // Update-Prüfung und Abgleich dürfen den Start nicht beenden: Ein Netzwerk- oder
            // Datenbankfehler landet im Protokoll, die Anwendung bleibt offen.
            try
            {
                if (UpdateService.AutoUpdateAllowed)
                {
                    await RunUpdateAsync(userInitiated: false);
                }

                await TrySyncAsync();
            }
            catch (Exception exception)
            {
                App.LogException("Start", exception);
                StatusText.Text = "Startprüfung fehlgeschlagen – Details im Protokoll.";
            }
            finally
            {
                _syncTimer.Start();
            }
        };
    }

    private void UpdateSyncText()
    {
        SyncText.Text = _services.Sync.IsConfigured
            ? $"Synchronisation: {_services.Sync.TransportName}"
            : "Synchronisation: lokal";
    }

    /// <summary>
    /// Gleicht die Datenbank ab. Fehler werden nur in der Statuszeile gemeldet –
    /// ohne erreichbaren Server soll die Anwendung normal weiterlaufen.
    /// </summary>
    private async Task TrySyncAsync()
    {
        if (_syncRunning || !_services.Sync.IsConfigured)
        {
            return;
        }

        _syncRunning = true;
        try
        {
            SyncText.Text = "Synchronisation: läuft …";
            var result = await _services.Sync.RunAsync();
            SyncText.Text = result.Succeeded
                ? $"Synchronisation: {result.Message}"
                : "Synchronisation: nicht möglich";
        }
        catch (Exception exception)
        {
            // Ohne erreichbaren Server läuft die Anwendung lokal weiter.
            App.LogException("Synchronisation", exception);
            SyncText.Text = "Synchronisation: nicht möglich";
        }
        finally
        {
            _syncRunning = false;
            UpdateSyncText();
        }
    }

    /// <summary>
    /// Offene Analyse-Texte sichern und zuletzt abgleichen; für den Abgleich höchstens
    /// 15 Sekunden warten. Beides darf das Beenden nicht verhindern.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _syncTimer.Stop();

        if (_closingAfterSync || _skipSyncOnClose)
        {
            return;
        }

        // Ohne offene Texte und ohne Abgleich schließt das Fenster sofort wie bisher.
        var analysisDirty = _analysisView is { HasUnsavedTexts: true };
        if (!analysisDirty && !_services.Sync.IsConfigured)
        {
            return;
        }

        e.Cancel = true;
        _closingAfterSync = true;

        try
        {
            // Die Texte kommen vor dem Abgleich in die Datenbank, damit er sie mitnimmt.
            if (analysisDirty)
            {
                StatusText.Text = "Analyse wird gespeichert …";
                await _analysisView!.SaveIfDirtyAsync();
            }

            if (_services.Sync.IsConfigured)
            {
                StatusText.Text = "Synchronisierung vor dem Beenden …";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _services.Sync.RunAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Zeitlimit erreicht: Das Schließen darf nicht daran scheitern.
        }
        catch (Exception exception)
        {
            // Auch ein Fehler beim Speichern hält das Beenden nicht auf.
            App.LogException("Beenden", exception);
        }
        finally
        {
            Close();
        }
    }

    private static TabItem CreateTab(string header, object content) =>
        new() { Header = header, Content = content };

    /// <summary>Erzeugt den Trennstrich zwischen zwei Themengruppen.</summary>
    private TabItem CreateSeparator() =>
        new() { Style = (Style)FindResource("LiftUpTabSeparator") };

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

            _skipSyncOnClose = true;
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
        var application = System.Windows.Application.Current;

        // Beim Abmelden wird das Hauptfenster geschlossen: Sonst bliebe der Inhalt des
        // abgemeldeten Benutzers offen, während davor nur das Anmeldefenster steht.
        // Die Anwendung läuft dabei weiter und beendet sich erst, wenn die Anmeldung
        // abgebrochen wird.
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _skipSyncOnClose = true;
        _syncTimer.Stop();
        Close();
        application.MainWindow = null;
        _services.ClearSession();

        var login = new LoginWindow(_services);
        if (login.ShowDialog() != true || _services.Session is null)
        {
            application.Shutdown();
            return;
        }

        var next = new MainWindow(_services);
        application.MainWindow = next;
        application.ShutdownMode = ShutdownMode.OnMainWindowClose;
        next.Show();
    }
}
