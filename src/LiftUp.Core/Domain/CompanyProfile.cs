namespace LiftUp.Core.Domain;

/// <summary>
/// Absenderdaten für Titelseiten: eigene Firma und Ansprechperson.
/// Wird einmal gepflegt und in Vorlagen über Platzhalter eingesetzt.
/// </summary>
public sealed class CompanyProfile
{
    public string Name { get; set; } = string.Empty;
    public string Contact { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) &&
        string.IsNullOrWhiteSpace(Contact) &&
        string.IsNullOrWhiteSpace(Email) &&
        string.IsNullOrWhiteSpace(Phone);
}
