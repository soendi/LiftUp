using System.IO;
using System.Reflection;
using System.Windows;
using LiftUp.Core.Backup;
using LiftUp.Core.Backup.Cloud;
using LiftUp.Core.Data;
using LiftUp.Core.Mail;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using LiftUp.Core.Sync;
using LiftUp.Core.Updates;

namespace LiftUp.App;

public partial class App : System.Windows.Application
{
    public static AppServices Services { get; private set; } = null!;

    /// <summary>Protokoll der unbehandelten Fehler – liegt neben der Datenbank im Benutzerprofil.</summary>
    public static string CrashLogPath { get; } =
        Path.Combine(AppPaths.GetApplicationDirectory(), "fehler.log");

    private static readonly object LogLock = new();

    /// <summary>
    /// Schreibt einen Fehler ins Protokoll. Ohne Protokoll beendet sich die Anwendung nach
    /// einem Fehler still, und niemand weiss, woran es lag.
    /// </summary>
    public static void LogException(string context, Exception exception)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
                File.AppendAllText(
                    CrashLogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {context}{Environment.NewLine}"
                    + exception + Environment.NewLine + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // Ein fehlendes Protokoll darf den Fehler nicht verdecken.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Ein unbehandelter Fehler soll die Anwendung nicht still beenden: Er landet im
        // Protokoll, wird gemeldet, und LiftUp läuft weiter.
        DispatcherUnhandledException += (_, args) =>
        {
            LogException("Oberfläche", args.Exception);
            MessageBox.Show(
                "LiftUp ist ein Fehler unterlaufen. Die Anwendung läuft weiter.\n\n"
                + args.Exception.Message + "\n\nProtokoll: " + CrashLogPath,
                "LiftUp", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                LogException("Hintergrund", exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("Aufgabe", args.Exception);
            args.SetObserved();
        };

        try
        {
            var database = new Database();
            database.EnsureCreated();
            Services = new AppServices(database);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Die lokale Datenbank konnte nicht geöffnet werden.\n\n" + exception.Message,
                "LiftUp", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var login = new LoginWindow(Services);
        var loggedIn = login.ShowDialog();
        if (loggedIn != true || Services.Session is null)
        {
            Shutdown();
            return;
        }

        try
        {
            var mainWindow = new MainWindow(Services);
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            LogException("Start", exception);
            MessageBox.Show(
                "Das Hauptfenster konnte nicht geöffnet werden.\n\n"
                + exception.Message + "\n\nProtokoll: " + CrashLogPath,
                "LiftUp", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }
}

/// <summary>Bündelt Datenbank und Dienste; wird nach der Anmeldung mit einer Sitzung versehen.</summary>
public sealed class AppServices(Database database)
{
    public Database Database { get; } = database;
    public AuthService Auth { get; } = new(database);
    // Ohne gespeicherte Zugangsdaten bleibt der Abgleich lokal; die Einstellungen
    // liest der Dienst bei jedem Lauf neu ein (Reiter „Datenbank“).
    public SyncService Sync { get; } = new(database);

    public UpdateService Updates { get; } = new(
        GitHubReleaseSource.Owner,
        GitHubReleaseSource.Repository,
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));

    public Session? Session { get; private set; }

    public Session CurrentSession => Session
        ?? throw new InvalidOperationException("Es ist kein Benutzer angemeldet.");

    public void SetSession(Session session) => Session = session;

    /// <summary>Notfallanmeldung mit MasterKey-Lizenzcode – ohne Benutzername und Passwort.</summary>
    public EmergencyAuthenticationService Emergency { get; } = new(database);

    /// <summary>Überwacht die Notfall-Sitzung; ohne Bedienung endet sie nach der eingestellten Zeit.</summary>
    public EmergencySessionManager? EmergencySession { get; private set; }

    public void SetEmergencySession(Session session)
    {
        Session = session;
        EmergencySession = new EmergencySessionManager(session, TimeSpan.FromMinutes(Emergency.TimeoutMinutes));
    }

    /// <summary>Beendet die Sitzung beim Abmelden; danach ist kein Benutzer mehr angemeldet.</summary>
    public void ClearSession()
    {
        Session = null;
        EmergencySession = null;
    }

    /// <summary>Die verbundene KI für die Überarbeitung von Texten.</summary>
    public AiService Ai { get; } = new(database);

    public ProjectService Projects => new(Database, CurrentSession);

    /// <summary>Zustandsanalyse und Investitionsplanung je Projekt.</summary>
    public ProjectTableService ProjectTables => new(Database, CurrentSession);

    /// <summary>Erfasste Texte der Analyse je Projekt.</summary>
    public ProjectAnalysisService Analysis => new(Database, CurrentSession);
    public UserService Users => new(Database, CurrentSession);
    public RoleService Roles => new(Database, CurrentSession);
    public TemplateService Templates => new(Database, CurrentSession);
    public DocumentTypeService DocumentTypes => new(Database, CurrentSession);
    public BackupService Backups { get; } = new(database);
    public CloudBackupService CloudBackups { get; } = new(database);
    public MailAccountService Mail => new(Database, CurrentSession);
    public ImageService Images => new(Database, CurrentSession);
    public SnippetService Snippets => new(Database, CurrentSession);
    public ChangeLogService ChangeLog => new(Database, CurrentSession);
    public DocumentService Documents => new(Database, CurrentSession, new Documents.WebView2PdfRenderer());
}
