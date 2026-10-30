namespace LiftUp.Core.Data;

public static class AppPaths
{
    public const string DatabaseFileName = "liftup.db";

    /// <summary>
    /// Speicherort der lokalen SQLite-Datenbank. Über die Umgebungsvariable
    /// LIFTUP_DB lässt sich der Pfad übersteuern (z. B. für Tests oder eine
    /// Ablage im Firmennetz).
    /// </summary>
    public static string GetDatabasePath()
    {
        var overridden = Environment.GetEnvironmentVariable("LIFTUP_DB");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = AppContext.BaseDirectory;
        }

        return Path.Combine(baseDirectory, "LiftUp", DatabaseFileName);
    }

    public static string GetApplicationDirectory() =>
        Path.GetDirectoryName(GetDatabasePath()) ?? AppContext.BaseDirectory;
}
