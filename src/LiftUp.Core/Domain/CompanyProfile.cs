namespace LiftUp.Core.Domain;

/// <summary>
/// Absenderdaten für Titelseiten: eigene Firma, Anschrift und Ansprechperson.
/// Wird einmal gepflegt und in Vorlagen über Platzhalter eingesetzt.
/// </summary>
public sealed class CompanyProfile
{
    public string Name { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public string Zip { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Contact { get; set; } = string.Empty;

    /// <summary>Pfad zum Firmenlogo; leer, wenn keines hinterlegt ist.</summary>
    public string LogoPath { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;

    /// <summary>„PLZ Ort“ in einer Zeile; Leeres entfällt.</summary>
    private string CityLine =>
        string.Join(" ", new[] { Zip, City }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Anschrift in einer Zeile, z. B. „Rietstrasse 56, 9443 Widnau“.</summary>
    public string Address =>
        string.Join(", ", new[] { Street, CityLine }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Anschrift in Zeilen: Straße und „PLZ Ort“ untereinander.</summary>
    public IReadOnlyList<string> AddressLines =>
        new[] { Street, CityLine }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!)
            .ToArray();

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) &&
        string.IsNullOrWhiteSpace(Street) &&
        string.IsNullOrWhiteSpace(Zip) &&
        string.IsNullOrWhiteSpace(City) &&
        string.IsNullOrWhiteSpace(Contact) &&
        string.IsNullOrWhiteSpace(LogoPath) &&
        string.IsNullOrWhiteSpace(Email) &&
        string.IsNullOrWhiteSpace(Phone);
}
