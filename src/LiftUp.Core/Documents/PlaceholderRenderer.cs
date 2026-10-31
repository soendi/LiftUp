using System.Text.RegularExpressions;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Documents;

/// <summary>Daten, die beim Rendern einer Vorlage zur Verfügung stehen.</summary>
public sealed class PlaceholderContext
{
    public required Project Project { get; init; }
    public required string UserName { get; init; }

    /// <summary>Eigene Absenderdaten für Titelseiten; optional.</summary>
    public CompanyProfile? Company { get; init; }
    public DateTime Now { get; init; } = DateTime.Now;

    /// <summary>Liefert den HTML-Inhalt eines Textbausteins (Name, ohne Präfix).</summary>
    public Func<string, string?>? SnippetResolver { get; init; }

    /// <summary>Liefert den HTML-Baustein eines Bildes (Name, ohne Präfix).</summary>
    public Func<string, string?>? ImageResolver { get; init; }
}

/// <summary>
/// Ersetzt <c>{{platzhalter}}</c> in HTML-Vorlagen. Werte aus der Datenbank werden
/// HTML-kodiert eingesetzt, damit ein Name mit Sonderzeichen das Layout nicht zerstört.
/// Textbausteine werden bewusst als HTML eingesetzt.
/// </summary>
public static class PlaceholderRenderer
{
    private static readonly Regex TokenPattern =
        new(@"\{\{\s*([A-Za-z0-9_.:\-]+)\s*\}\}", RegexOptions.Compiled);

    public const string SnippetPrefix = "snippet:";
    public const string ImagePrefix = "bild:";
    public const string ImagePrefixEnglish = "image:";

    /// <summary>Übersicht für den Vorlageneditor.</summary>
    public static IReadOnlyList<(string Token, string Description)> Known { get; } =
    [
        ("{{project.number}}", "Projektnummer"),
        ("{{project.name}}", "Projektname"),
        ("{{project.customer}}", "Kunde"),
        ("{{project.address}}", "Anschrift (Straße, PLZ Ort)"),
        ("{{project.street}}", "Straße und Hausnummer"),
        ("{{project.zip}}", "Postleitzahl"),
        ("{{project.city}}", "Ort"),
        ("{{project.contact}}", "Erste Ansprechperson"),
        ("{{project.contacts}}", "Alle Ansprechpersonen untereinander"),
        ("{{project.responsible}}", "Verantwortlicher"),
        ("{{project.description}}", "Beschreibung"),
        ("{{project.status}}", "Status"),
        ("{{project.folder}}", "Projektordner"),
        ("{{company.name}}", "Eigene Firma (aus dem Benutzerprofil)"),
        ("{{company.contact}}", "Anzeigename des Benutzers"),
        ("{{company.email}}", "Eigene E-Mail-Adresse (Benutzerprofil)"),
        ("{{company.phone}}", "Eigene Telefonnummer (Benutzerprofil)"),
        ("{{date}}", "Datum (TT.MM.JJJJ)"),
        ("{{datetime}}", "Datum und Uhrzeit"),
        ("{{user}}", "Angemeldeter Benutzer"),
        ("{{snippet:Name}}", "Textbaustein mit diesem Namen"),
        ("{{bild:Name}}", "Bild aus der Bibliothek"),
    ];

    public static IReadOnlyList<string> FindPlaceholders(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        return TokenPattern.Matches(html)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(token => token, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Bildnamen aus <c>{{bild:Name}}</c> bzw. <c>{{image:Name}}</c>, ohne Doppelte.
    /// Grundlage für die Prüfung vor dem Erzeugen eines PDFs.
    /// </summary>
    public static IReadOnlyList<string> FindImageNames(string? html) =>
        FindNames(html, ImagePrefix, ImagePrefixEnglish);

    /// <summary>
    /// Namen der Textbausteine aus <c>{{snippet:Name}}</c>, ohne Doppelte.
    /// Grundlage für die Prüfung vor dem Erzeugen eines PDFs.
    /// </summary>
    public static IReadOnlyList<string> FindSnippetNames(string? html) =>
        FindNames(html, SnippetPrefix);

    private static IReadOnlyList<string> FindNames(string? html, params string[] prefixes) =>
        FindPlaceholders(html ?? string.Empty)
            .Select(token => prefixes
                .Where(prefix => token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(prefix => token[prefix.Length..].Trim())
                .FirstOrDefault(name => name.Length > 0))
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string Render(string html, PlaceholderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        return TokenPattern.Replace(html, match => Resolve(match.Groups[1].Value, context));
    }

    private static string Resolve(string token, PlaceholderContext context)
    {
        var project = context.Project;
        var key = token.ToLowerInvariant();

        if (key.StartsWith(SnippetPrefix, StringComparison.Ordinal))
        {
            var name = token[SnippetPrefix.Length..];
            return context.SnippetResolver?.Invoke(name) ?? string.Empty;
        }

        if (key.StartsWith(ImagePrefix, StringComparison.Ordinal))
        {
            return context.ImageResolver?.Invoke(token[ImagePrefix.Length..]) ?? string.Empty;
        }

        if (key.StartsWith(ImagePrefixEnglish, StringComparison.Ordinal))
        {
            return context.ImageResolver?.Invoke(token[ImagePrefixEnglish.Length..]) ?? string.Empty;
        }

        return key switch
        {
            "project.number" or "projekt.nummer" => Encode(project.Number),
            "project.name" or "projekt.name" => Encode(project.Name),
            "project.customer" or "projekt.kunde" => Encode(project.Customer),
            "project.street" or "project.customerstreet" or "projekt.strasse" or "projekt.anschrift" => Encode(project.CustomerStreet),
            "project.zip" or "project.customerzip" or "projekt.plz" => Encode(project.CustomerZip),
            "project.city" or "project.customercity" or "projekt.ort" => Encode(project.CustomerCity),
            "project.address" or "project.customeraddress" or "projekt.adresse" => Encode(project.CustomerAddress),
            "project.contact" or "projekt.ansprechpartner" =>
                Encode(project.Contact ?? project.Contacts.FirstOrDefault()?.Summary),
            "project.contacts" or "projekt.ansprechpersonen" => RenderContacts(project.Contacts),
            "project.responsible" or "projekt.verantwortlich" => Encode(project.ResponsibleName),
            "project.description" or "projekt.beschreibung" => Encode(project.Description),
            "project.status" or "projekt.status" => Encode(ProjectStatusCatalog.Describe(project.Status)),
            "project.folder" or "projekt.ordner" => Encode(project.FolderPath),
            "company.name" or "firma" or "firma.name" => Encode(context.Company?.Name),
            "company.contact" or "company.person" or "firma.ansprechperson" => Encode(context.Company?.Contact),
            "company.email" or "firma.email" => Encode(context.Company?.Email),
            "company.phone" or "firma.telefon" => Encode(context.Company?.Phone),
            "date" or "datum" => context.Now.ToString("dd.MM.yyyy"),
            "time" or "zeit" => context.Now.ToString("HH:mm"),
            "datetime" or "datumzeit" => context.Now.ToString("dd.MM.yyyy HH:mm"),
            "user" or "benutzer" => Encode(context.UserName),
            _ => string.Empty,
        };
    }

    /// <summary>Ansprechpersonen untereinander; jede Zeile ist HTML-kodiert.</summary>
    private static string RenderContacts(IReadOnlyList<ProjectContact> contacts)
    {
        if (contacts.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("<br />", contacts.Select(contact => Encode(contact.Summary)));
    }

    /// <summary>
    /// Escaped nur die fünf kritischen Zeichen. Umlaute bleiben stehen, weil die
    /// Dokumente durchgängig UTF-8 sind – das hält Vorlagen und PDF lesbar.
    /// </summary>
    private static string Encode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }
}
