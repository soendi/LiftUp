namespace LiftUp.Core.Domain;

/// <summary>
/// Fehlende Angaben einer Vorlage vor dem Erzeugen eines PDFs, getrennt nach Bildern und
/// Textbausteinen. Eine leere Liste heißt jeweils „alles vorhanden“.
/// </summary>
public sealed record MissingPlaceholders(
    IReadOnlyList<string> Images,
    IReadOnlyList<string> Snippets)
{
    public static MissingPlaceholders Empty { get; } = new([], []);

    public bool Any => Images.Count > 0 || Snippets.Count > 0;
}
