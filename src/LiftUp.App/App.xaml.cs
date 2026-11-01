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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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

        var mainWindow = new MainWindow(Services);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
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

    /// <summary>Beendet die Sitzung beim Abmelden; danach ist kein Benutzer mehr angemeldet.</summary>
    public void ClearSession() => Session = null;

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
