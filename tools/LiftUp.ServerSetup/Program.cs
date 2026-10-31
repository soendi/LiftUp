using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using LiftUp.Core.Domain;
using LiftUp.Core.Sync;

namespace LiftUp.ServerSetup;

/// <summary>
/// Richtet die zentrale LiftUp-Datenbank auf einem Windows-Server ein:
/// PostgreSQL in der neuesten Version holen (über winget, also immer die
/// aktuelle Fassung), Datenbank und Schema anlegen und eine Datei mit den
/// Zugangsdaten ablegen – standardmäßig auf dem Desktop.
///
/// Aufruf:
///   LiftUp.ServerSetup.exe
///   LiftUp.ServerSetup.exe --port 5432 --database liftup --user liftup
///   LiftUp.ServerSetup.exe --whatif
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // Ohne Konsole (etwa im Setup) nicht kritisch.
        }

        // Alles zusätzlich in eine Datei schreiben: Bei einem Doppelklick schließt
        // sich das Konsolenfenster sofort, danach wäre kein Fehlschlag mehr zu erklären.
        var console = Console.Out;
        var logWriter = SetupLog.TryOpen();
        if (logWriter is not null)
        {
            Console.SetOut(new TeeWriter(console, logWriter));
        }

        Console.WriteLine();
        Console.WriteLine("LiftUp – Server-Datenbank einrichten");
        Console.WriteLine("===================================");
        if (SetupLog.CurrentPath is { } logPath)
        {
            Console.WriteLine($"Protokoll: {logPath}");
        }

        SetupOptions options;
        try
        {
            options = SetupOptions.Parse(args);
        }
        catch (ValidationException exception)
        {
            Console.WriteLine(exception.Message);
            SetupOptions.PrintHelp();
            return 1;
        }

        if (options.ShowHelp)
        {
            SetupOptions.PrintHelp();
            return 0;
        }

        if (!IsAdministrator())
        {
            Console.WriteLine("Hinweis: Ohne Administratorrechte lässt sich PostgreSQL nicht installieren.");
            Console.WriteLine("         Bitte das Setup über „Als Administrator ausführen“ starten.");
            Console.WriteLine();
        }

        var installed = PostgreSqlLocator.IsInstalled(options.Port);

        if (installed)
        {
            Console.WriteLine("PostgreSQL ist bereits vorhanden – es wird nichts installiert.");
        }
        else
        {
            // Nur wenn wirklich installiert wird, braucht das Werkzeug eine Netzanbindung.
            PostgresRelease release;
            try
            {
                Console.WriteLine("Ermittle die neueste PostgreSQL-Version …");
                release = await PostgresReleaseService.FetchLatestAsync();
            }
            catch (ValidationException exception)
            {
                Console.WriteLine(exception.Message);
                Console.WriteLine("Ohne diese Angabe lässt sich PostgreSQL nicht installieren.");
                return 2;
            }

            Console.WriteLine($"Neueste Version laut postgresql.org: {release.Version}");

            var package = PostgresReleaseService.WingetPackageId(release);
            Console.WriteLine($"PostgreSQL fehlt. Installiert wird {package} (neueste Hauptversion).");

            if (options.DryRun)
            {
                Console.WriteLine($"[WhatIf] winget install --id {package} --silent …");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(options.SuperuserPassword))
                {
                    Console.Write("Passwort des Benutzers „postgres“ (wird bei der Installation gesetzt): ");
                    options = options with { SuperuserPassword = Console.ReadLine() ?? string.Empty };
                }

                if (string.IsNullOrWhiteSpace(options.SuperuserPassword))
                {
                    Console.WriteLine("Ohne Passwort für „postgres“ kann PostgreSQL nicht eingerichtet werden.");
                    return 3;
                }

                var exitCode = await WingetInstaller.InstallAsync(package, options.Port, options.SuperuserPassword);
                if (exitCode != 0)
                {
                    Console.WriteLine($"Die Installation ist fehlgeschlagen (Exitcode {exitCode}).");
                    Console.WriteLine($"Bitte PostgreSQL manuell installieren: https://www.postgresql.org/download/windows/");
                    return 3;
                }

                if (!await WaitForPortAsync("127.0.0.1", options.Port, TimeSpan.FromMinutes(3)))
                {
                    Console.WriteLine($"Auf Port {options.Port} antwortet nach der Installation kein PostgreSQL.");
                    return 4;
                }
            }
        }

        var target = new DatabaseServerSettings
        {
            Host = options.PublicHost,
            Port = options.Port,
            Database = options.Database,
            User = options.User,
            Password = options.Password,
            SslMode = "Prefer",
        };

        var administrator = new DatabaseServerSettings
        {
            Host = options.ConnectHost,
            Port = options.Port,
            Database = "postgres",
            User = "postgres",
            Password = options.SuperuserPassword,
            SslMode = "Prefer",
        };

        if (options.DryRun)
        {
            Console.WriteLine($"[WhatIf] Datenbank „{target.Database}“ und Benutzer „{target.User}“ würden angelegt.");
            Console.WriteLine($"[WhatIf] Zugangsdatei würde nach {options.OutputPath ?? ConnectionFile.SuggestPath()} geschrieben.");
            return 0;
        }

        // Das Passwort wird auch dann gebraucht, wenn PostgreSQL schon läuft und nur
        // Datenbank und Benutzer fehlen – früher endete der Lauf hier ohne Nachfrage.
        if (string.IsNullOrWhiteSpace(options.SuperuserPassword))
        {
            Console.Write("Passwort des Benutzers „postgres“ (Superuser): ");
            options = options with { SuperuserPassword = Console.ReadLine() ?? string.Empty };
        }

        if (string.IsNullOrWhiteSpace(options.SuperuserPassword))
        {
            Console.WriteLine("Für das Anlegen von Datenbank und Benutzer wird das Passwort von „postgres“ benötigt.");
            Console.WriteLine("Das Passwort von Hand eingeben oder --superuser-password angeben.");
            return 5;
        }

        administrator = administrator with { Password = options.SuperuserPassword };

        Console.WriteLine();
        Console.WriteLine("Lege Datenbank, Benutzer und Schema an …");
        ProvisionResult? provisioned = null;
        try
        {
            provisioned = await new ServerProvisioner(administrator, target).ProvisionAsync();
            Console.WriteLine($"PostgreSQL {provisioned.ServerVersion} ist bereit.");
            Console.WriteLine(provisioned.RoleCreated
                ? $"Benutzer „{target.User}“ wurde angelegt."
                : $"Benutzer „{target.User}“ war bereits vorhanden.");
            Console.WriteLine(provisioned.DatabaseCreated
                ? $"Datenbank „{target.Database}“ wurde angelegt."
                : $"Datenbank „{target.Database}“ war bereits vorhanden.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.WriteLine($"Einrichten fehlgeschlagen: {exception.Message}");
            return 6;
        }

        var path = options.OutputPath ?? ConnectionFile.SuggestPath();
        var written = false;
        try
        {
            Console.WriteLine();
            Console.WriteLine($"Schreibe Zugangsdatei nach {path} …");
            ConnectionFile.From(target, provisioned?.ServerVersion).Save(path);
            written = true;
            Console.WriteLine($"Zugangsdatei geschrieben: {path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Die Zugangsdatei konnte nicht geschrieben werden: {exception.Message}");
            Console.WriteLine("Die Daten stehen unten – bitte von Hand notieren.");
        }

        Console.WriteLine();
        Console.WriteLine("Fertig. So verbinden sich die Arbeitsplätze:");
        Console.WriteLine($"  Server:    {target.Host}");
        Console.WriteLine($"  Port:      {target.Port}");
        Console.WriteLine($"  Datenbank: {target.Database}");
        Console.WriteLine($"  Benutzer:  {target.User}");
        Console.WriteLine($"  Passwort:  {options.Password}");
        Console.WriteLine();
        Console.WriteLine("In LiftUp den Reiter „Datenbank“ öffnen und die Datei einlesen –");
        Console.WriteLine("oder die Werte von Hand eintragen und speichern.");
        Console.WriteLine("Falls die Windows-Firewall fragt: Port 5432 für das lokale Netz freigeben.");
        return written ? 0 : 7;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task<bool> WaitForPortAsync(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (PostgreSqlLocator.PortAnswers(host, port))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return false;
    }
}

/// <summary>Kommandozeilenoptionen des Server-Setups.</summary>
internal sealed record SetupOptions
{
    public int Port { get; init; } = DatabaseServerSettings.DefaultPort;
    public string Database { get; init; } = DatabaseServerSettings.DefaultDatabase;
    public string User { get; init; } = DatabaseServerSettings.DefaultUser;
    public string Password { get; init; } = GeneratePassword();
    public string SuperuserPassword { get; init; } = string.Empty;
    public string ConnectHost { get; init; } = "127.0.0.1";
    public string PublicHost { get; init; } = Environment.MachineName;
    public string? OutputPath { get; init; }
    public bool DryRun { get; init; }
    public bool ShowHelp { get; init; }

    public static SetupOptions Parse(string[] args)
    {
        var options = new SetupOptions();

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--port":
                    options = options with { Port = ReadInt(args, ref index) };
                    break;
                case "--database":
                    options = options with { Database = RequireValue(args, ref index) };
                    break;
                case "--user":
                    options = options with { User = RequireValue(args, ref index) };
                    break;
                case "--password":
                    options = options with { Password = RequireValue(args, ref index) };
                    break;
                case "--superuser-password":
                    options = options with { SuperuserPassword = RequireValue(args, ref index) };
                    break;
                case "--connect-host":
                    options = options with { ConnectHost = RequireValue(args, ref index) };
                    break;
                case "--host":
                    options = options with { PublicHost = RequireValue(args, ref index) };
                    break;
                case "--output":
                    options = options with { OutputPath = RequireValue(args, ref index) };
                    break;
                case "--whatif":
                case "--dry-run":
                    options = options with { DryRun = true };
                    break;
                case "--help":
                case "-h":
                case "/?":
                    options = options with { ShowHelp = true };
                    break;
                default:
                    throw new ValidationException($"Unbekannte Option „{argument}“.");
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            Aufruf:
              LiftUp.ServerSetup.exe [Optionen]

            Optionen:
              --port <Zahl>              Port des PostgreSQL-Servers (Standard 5432)
              --database <Name>          Name der Datenbank (Standard liftup)
              --user <Name>              Benutzer für die Clients (Standard liftup)
              --password <Text>          Passwort dieses Benutzers (sonst zufällig erzeugt)
              --superuser-password <Text> Passwort des Benutzers „postgres“
              --connect-host <Host>      Adresse für die Einrichtung (Standard 127.0.0.1)
              --host <Host>              Adresse, die in der Zugangsdatei steht (Standard: Rechnername)
              --output <Pfad>            Ablageort der Zugangsdatei (Standard: Desktop)
              --whatif                   Nur zeigen, was getan würde
              --help                     Diese Übersicht

            Protokoll: %ProgramData%\LiftUp\logs (ersatzweise der Temp-Ordner)
            Rückgabewerte: 0 fertig, 1 Optionen, 2 Version/Netz, 3 Installation,
                           4 Port antwortet nicht, 5 Passwort fehlt, 6 Einrichten
                           fehlgeschlagen, 7 Zugangsdatei nicht geschrieben
            """);
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ValidationException($"Zur Option „{args[index]}“ fehlt der Wert.");
        }

        index++;
        return args[index].Trim();
    }

    private static int ReadInt(string[] args, ref int index)
    {
        var value = RequireValue(args, ref index);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ||
            number is <= 0 or > 65535)
        {
            throw new ValidationException($"„{value}“ ist kein gültiger Port.");
        }

        return number;
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var characters = new char[20];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(characters);
    }
}

/// <summary>Erkennt eine vorhandene PostgreSQL-Installation.</summary>
internal static class PostgreSqlLocator
{
    public static bool IsInstalled(int port) =>
        PortAnswers("127.0.0.1", port) || ServiceExists() || ProgramFolderExists();

    public static bool PortAnswers(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            return connect.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch (Exception exception) when (exception is SocketException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool ServiceExists()
    {
        try
        {
            return ServiceController.GetServices().Any(service =>
                service.ServiceName.StartsWith("postgresql", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException
                                              or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Prüft, ob unter „Program Files\PostgreSQL“ wirklich Binärdateien liegen.
    /// Ein leerer Restordner – etwa nach einer Deinstallation oder auf einem
    /// vorbereiteten Rechnerimage – darf die Installation nicht blockieren.
    /// </summary>
    private static bool ProgramFolderExists()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PostgreSQL");
        if (!Directory.Exists(folder))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(folder, "pg_ctl.exe", SearchOption.AllDirectories).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Installiert PostgreSQL still über winget – ohne festgeschriebene Version.</summary>
internal static class WingetInstaller
{
    public static async Task<int> InstallAsync(string packageId, int port, string superuserPassword)
    {
        var startInfo = new ProcessStartInfo("winget")
        {
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--id");
        startInfo.ArgumentList.Add(packageId);
        startInfo.ArgumentList.Add("--exact");
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--accept-package-agreements");
        startInfo.ArgumentList.Add("--accept-source-agreements");
        startInfo.ArgumentList.Add("--override");
        startInfo.ArgumentList.Add(
            $"--mode unattended --unattendedmodeui none --serverport {port} --superpassword \"{superuserPassword}\"");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.WriteLine("winget konnte nicht gestartet werden.");
                return -1;
            }

            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("winget ist auf diesem Rechner nicht verfügbar.");
            Console.WriteLine("Bitte PostgreSQL von Hand installieren: https://www.postgresql.org/download/windows/");
            return -1;
        }
    }
}

/// <summary>
/// Schreibt alles, was das Werkzeug ausgibt, zusätzlich in eine Protokolldatei.
/// Ein per Doppelklick gestartetes Konsolenfenster schließt sich sofort; ohne
/// diese Datei wäre ein Fehlschlag später nicht mehr nachvollziehbar.
/// </summary>
internal static class SetupLog
{
    public static string? CurrentPath { get; private set; }

    public static StreamWriter? TryOpen()
    {
        foreach (var folder in new[]
                 {
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         "LiftUp",
                         "logs"),
                     Path.GetTempPath(),
                 })
        {
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(
                    folder,
                    $"ServerSetup-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
                var writer = new StreamWriter(path, append: false) { AutoFlush = true };
                CurrentPath = path;
                return writer;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Nächsten Ort versuchen.
            }
        }

        return null;
    }
}

/// <summary>Leitet die Ausgabe gleichzeitig an die Konsole und die Protokolldatei.</summary>
internal sealed class TeeWriter(TextWriter console, TextWriter file) : TextWriter
{
    public override Encoding Encoding => console.Encoding;

    public override void Write(char value)
    {
        console.Write(value);
        file.Write(value);
    }

    public override void Write(string? value)
    {
        console.Write(value);
        file.Write(value);
    }

    public override void WriteLine(string? value)
    {
        console.WriteLine(value);
        file.WriteLine(value);
    }

    public override void Flush()
    {
        console.Flush();
        file.Flush();
    }
}
