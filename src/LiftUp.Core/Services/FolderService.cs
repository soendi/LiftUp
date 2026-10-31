namespace LiftUp.Core.Services;

/// <summary>
/// Umgang mit Projektordnern. Ein Projektordner darf auf jedem Laufwerk liegen
/// (lokal, Netzlaufwerk, Wechseldatenträger); geprüft wird nur, was ohne
/// Seiteneffekt prüfbar ist.
/// </summary>
public static class FolderService
{
    /// <summary>Wandelt eine Eingabe in einen absoluten Pfad um und meldet Formatfehler.</summary>
    public static string Normalize(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Bitte einen Projektordner angeben.";
            return string.Empty;
        }

        var trimmed = path.Trim().Trim('"');

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Der Pfad ist ungültig. Beispiel: D:\\Projekte\\Neubau Musterweg";
            return string.Empty;
        }

        if (fullPath.Length > 240)
        {
            error = "Der Pfad ist zu lang (mehr als 240 Zeichen). Bitte einen kürzeren Ordner wählen.";
            return string.Empty;
        }

        if (File.Exists(fullPath))
        {
            error = "Der Pfad zeigt auf eine Datei, nicht auf einen Ordner.";
            return string.Empty;
        }

        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                error = "Das Laufwerk ist nicht verfügbar. Bitte ein verbundenes Laufwerk wählen.";
                return string.Empty;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            error = "Das Laufwerk konnte nicht geprüft werden.";
            return string.Empty;
        }

        return fullPath;
    }

    /// <summary>
    /// Legt den Projektordner samt der üblichen Unterordner an. Vorhandene Ordner
    /// bleiben unangetastet, damit ein bestehender Projektordner nichts verliert.
    /// </summary>
    public static IReadOnlyList<string> EnsureStandardStructure(string fullPath)
    {
        EnsureCreated(fullPath);

        var created = new List<string>();
        foreach (var name in Domain.ProjectFolders.Standard)
        {
            var path = Path.Combine(fullPath, name);
            if (Directory.Exists(path))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(path);
                created.Add(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new Domain.ValidationException(
                    $"Der Unterordner \"{name}\" konnte nicht angelegt werden: {exception.Message}");
            }
        }

        return created;
    }

    public static void EnsureCreated(string fullPath)
    {
        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new Domain.ValidationException(
                $"Der Ordner konnte nicht angelegt werden: {exception.Message}");
        }
    }

    /// <summary>
    /// Löscht eine einzelne Datei; unter Windows landet sie im Papierkorb, damit ein
    /// versehentliches Löschen rückgängig gemacht werden kann.
    /// </summary>
    public static bool TryDeleteFile(string fullPath, out string? error)
    {
        error = null;
        if (!File.Exists(fullPath))
        {
            return true;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    fullPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                File.Delete(fullPath);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            error = $"Die Datei konnte nicht gelöscht werden: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Verschiebt einen Ordner in den Papierkorb. Nur unter Windows möglich und nur,
    /// wenn der Benutzer das ausdrücklich verlangt hat.
    /// </summary>
    public static bool TryMoveToRecycleBin(string fullPath, out string? error)
    {
        error = null;
        if (!Directory.Exists(fullPath))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            error = "Papierkorb wird nur unter Windows unterstützt.";
            return false;
        }

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                fullPath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            error = $"Der Ordner konnte nicht in den Papierkorb verschoben werden: {exception.Message}";
            return false;
        }
    }
}
