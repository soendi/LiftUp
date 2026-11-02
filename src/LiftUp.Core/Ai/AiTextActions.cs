namespace LiftUp.Core.Ai;

/// <summary>Die vier Überarbeitungen, die über jedem Text-Editor angeboten werden.</summary>
public enum AiTextAction
{
    /// <summary>Rechtschreibung, Grammatik und Zeichensetzung.</summary>
    Correct,

    /// <summary>Professionellere, sachlichere Formulierung.</summary>
    Professional,

    /// <summary>Auf das Wesentliche gekürzt.</summary>
    Shorten,

    /// <summary>Ausführlicher, ohne neue Fakten.</summary>
    Expand,

    /// <summary>Aus Stichworten einen zusammenhängenden Text formulieren.</summary>
    FromKeywords,
}

public static class AiTextActions
{
    /// <summary>Beschriftung im Auswahlfeld über dem Editor und im Vergleichsfenster.</summary>
    public static string Label(AiTextAction action) => action switch
    {
        AiTextAction.Correct => "Text korrigieren",
        AiTextAction.Professional => "Professioneller",
        AiTextAction.Shorten => "Text kürzen",
        AiTextAction.Expand => "Text verlängern",
        AiTextAction.FromKeywords => "Aus Stichworten Text erstellen",
        _ => "Text überarbeiten",
    };

    /// <summary>Vorgabe an das Modell, worauf es beim Überarbeiten achten soll.</summary>
    public static string Instruction(AiTextAction action) => action switch
    {
        AiTextAction.Correct =>
            "Korrigiere Rechtschreibung, Grammatik und Zeichensetzung. Ändere weder Inhalt noch Stil " +
            "und behalte die Absätze bei.",
        AiTextAction.Professional =>
            "Formuliere den Text professioneller und sachlicher, wie in einem technischen Bericht. " +
            "Behalte Inhalt, Zahlen, Bezeichnungen und die Absätze bei.",
        AiTextAction.Shorten =>
            "Kürze den Text auf das Wesentliche, ohne eine Aussage zu verlieren. Behalte Zahlen, " +
            "Bezeichnungen und die Reihenfolge der Absätze bei.",
        AiTextAction.Expand =>
            "Formuliere den Text ausführlicher und ergänze passende fachliche Wendungen. Erfinde keine " +
            "Fakten, Zahlen oder Normen und behalte den Stil bei.",
        AiTextAction.FromKeywords =>
            "Formuliere aus den folgenden Stichworten einen zusammenhängenden Fachtext in ganzen Sätzen " +
            "und sinnvoller Reihenfolge. Behalte alle Angaben, Zahlen und Bezeichnungen bei, ergänze " +
            "nichts, was nicht in den Stichworten steht, und gliedere längere Texte in Absätze.",
        _ => "Überarbeite den Text, ohne seinen Inhalt zu verändern.",
    };

    /// <summary>
    /// Gemeinsame Anweisung an das Modell: deutscher Fachtext, nur der überarbeitete Text als
    /// Antwort – so lässt sich das Ergebnis ohne Nachbearbeitung übernehmen.
    /// </summary>
    public const string SystemPrompt =
        "Du überarbeitest deutsche Fachtexte aus der Aufzugsbranche (Wartung, Prüfung, Instandsetzung, " +
        "Modernisierung). Antworte ausschließlich mit dem überarbeiteten Text – ohne Vorrede, ohne " +
        "Kommentar, ohne Anführungszeichen und ohne Markdown-Auszeichnung. Behalte die Absätze bei. " +
        "Erfinde keine Fakten, Zahlen oder Normen.";
}
