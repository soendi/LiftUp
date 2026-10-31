using System.Text.Json;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Documents;

/// <summary>
/// Hält die ausgefüllten Abschnitte eines Dokuments als JSON, damit ein
/// erzeugtes Dokument später erneut geöffnet und geändert werden kann.
/// </summary>
public static class DocumentSections
{
    public static string Serialize(IReadOnlyList<DocumentSectionContent> sections)
    {
        var payload = (sections ?? [])
            .Select(section => new SectionDto { Title = section.Title, Html = section.Html })
            .ToArray();

        return JsonSerializer.Serialize(payload);
    }

    public static IReadOnlyList<DocumentSectionContent> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var payload = JsonSerializer.Deserialize<SectionDto[]>(json);
            if (payload is null)
            {
                return [];
            }

            return payload
                .Select(section => new DocumentSectionContent(section.Title ?? string.Empty, section.Html ?? string.Empty))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class SectionDto
    {
        public string? Title { get; set; }
        public string? Html { get; set; }
    }
}
