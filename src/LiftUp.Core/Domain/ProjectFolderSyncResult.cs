namespace LiftUp.Core.Domain;

/// <summary>
/// Ergebnis eines Ordnerabgleichs: neu aufgenommene Bilder und solche, die an
/// anderer Stelle im Projektordner wiedergefunden wurden.
/// </summary>
public sealed record ProjectFolderSyncResult(int Added, int Moved)
{
    public int Total => Added + Moved;

    public bool Any => Total > 0;
}
