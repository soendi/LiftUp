namespace LiftUp.App;

/// <summary>
/// Das in den Reitern gewählte Projekt. Zustandsanalyse, Investitionsplanung, Analyse und
/// PDF teilen sich diese Auswahl: Ein Wechsel in einem Reiter zieht die anderen sofort mit.
/// </summary>
public sealed class ProjectSelection
{
    /// <summary>Gewähltes Projekt; <c>null</c>, solange keines zur Verfügung steht.</summary>
    public Guid? ProjectId { get; private set; }

    /// <summary>Meldet einen Wechsel für alle Reiter.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Meldet, dass sich die Projektliste geändert hat (neues oder geändertes Projekt).
    /// Die Reiter lesen ihre Auswahl danach neu ein; die gemeinsame Auswahl bleibt bestehen.
    /// </summary>
    public event EventHandler? ProjectsChanged;

    /// <summary>Lässt alle Reiter ihre Projektliste neu einlesen.</summary>
    public void NotifyProjectsChanged() => ProjectsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Setzt die Auswahl und meldet sie den anderen Reitern. Liefert <c>false</c>, wenn sie
    /// schon gilt – dann braucht kein Steuerelement nachzuziehen.
    /// </summary>
    public bool Select(Guid? projectId)
    {
        if (ProjectId == projectId)
        {
            return false;
        }

        ProjectId = projectId;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
