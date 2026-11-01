namespace LiftUp.App.Views;

/// <summary>
/// Bearbeitbare Zeile der Meilenstein-Tabelle; sie wird im Projektdialog und in der
/// Projektübersicht verwendet.
/// </summary>
public sealed class MilestoneRow
{
    public string Title { get; set; } = string.Empty;
    public bool IsDone { get; set; }

    /// <summary>Fester Meilenstein (Begehung): Bezeichnung und Zeile sind nicht änderbar.</summary>
    public bool IsFixed { get; set; }

    /// <summary>Datum als Text in der Form TT.MM.JJJJ.</summary>
    public string DoneDateText { get; set; } = string.Empty;
}
