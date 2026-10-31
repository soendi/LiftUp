using System.Text;

namespace LiftUp.Core.Documents;

/// <summary>Anzeigegröße eines Bildes im Dokument.</summary>
public enum ImageSize
{
    Small,
    Medium,
    Large,
}

/// <summary>Waagerechte Platzierung eines Bildes auf der Seite.</summary>
public enum ImagePlacement
{
    Left,
    Center,
    Right,
    BelowText,
}

/// <summary>Wie der Text um ein Bild herum läuft.</summary>
public enum ImageFlow
{
    Inline,
    Square,
    Tight,
    Through,
    TopAndBottom,
}

/// <summary>
/// Layout-Angaben zu einem Bild im Dokument. Größe, Seite, Textfluss und die
/// optionale Bildunterschrift stehen als Klassen und Datenattribute im HTML;
/// der Editor liest sie beim Anklicken wieder, und das PDF nutzt dieselben Regeln.
/// </summary>
public sealed record ImageFormat(
    ImageSize Size = ImageSize.Medium,
    ImagePlacement Placement = ImagePlacement.Center,
    ImageFlow Flow = ImageFlow.Inline,
    string? Caption = null)
{
    /// <summary>Vorgabe für ein neu eingefügtes Bild: mittel, zentriert, im Textfluss.</summary>
    public static ImageFormat Default { get; } = new();

    /// <summary>Regeln für alle Bild-Layouts; Editor und PDF binden dieselben Klassen ein.</summary>
    public const string Css = """
        .liftup-fig {
          margin: 0 0 4mm 0;
          max-width: 100%;
          break-inside: avoid;
          page-break-inside: avoid;
        }
        .liftup-fig img { display: block; width: 100%; height: auto; max-width: 100%; }
        .liftup-fig figcaption {
          margin-top: 1.5mm;
          font-size: 9pt;
          font-style: italic;
          color: #4A5568;
          line-height: 1.3;
        }
        /* Die Größe bezieht sich auf eine volle Zeile (Seitenbreite ohne Rand). */
        .liftup-fig-small { width: 33.3333%; }
        .liftup-fig-medium { width: 50%; }
        .liftup-fig-large { width: 66.6667%; }
        .liftup-fig-inline { display: inline-block; vertical-align: middle; margin: 0 3mm 2mm 0; }
        .liftup-fig-center:not(.liftup-fig-inline) { display: block; margin-left: auto; margin-right: auto; }
        /* Mit Unterschrift steht das Bild als eigener Block im Text: Bild und
           Unterschrift bleiben eine Einheit, der Text steht darüber und darunter. */
        .liftup-fig-captioned.liftup-fig-inline { display: block; }
        .liftup-fig-captioned.liftup-fig-inline.liftup-fig-left { margin: 0 auto 4mm 0; }
        .liftup-fig-captioned.liftup-fig-inline.liftup-fig-center { margin: 0 auto 4mm auto; }
        .liftup-fig-captioned.liftup-fig-inline.liftup-fig-right { margin: 0 0 4mm auto; }
        .liftup-fig-square.liftup-fig-left,
        .liftup-fig-tight.liftup-fig-left,
        .liftup-fig-through.liftup-fig-left { float: left; }
        .liftup-fig-square.liftup-fig-right,
        .liftup-fig-tight.liftup-fig-right,
        .liftup-fig-through.liftup-fig-right { float: right; }
        .liftup-fig-square.liftup-fig-left { margin: 0 5mm 3mm 0; }
        .liftup-fig-square.liftup-fig-right { margin: 0 0 3mm 5mm; }
        .liftup-fig-tight.liftup-fig-left { margin: 0 2mm 2mm 0; }
        .liftup-fig-tight.liftup-fig-right { margin: 0 0 2mm 2mm; }
        .liftup-fig-through.liftup-fig-left { margin: 0 0 2mm 0; }
        .liftup-fig-through.liftup-fig-right { margin: 0 0 2mm 0; }
        .liftup-fig-topbottom { display: block; clear: both; }
        .liftup-fig-topbottom.liftup-fig-left { margin-right: auto; }
        .liftup-fig-topbottom.liftup-fig-right { margin-left: auto; }
        .liftup-fig-below { display: block; clear: both; }
        .liftup-fig-strip {
          display: flex;
          align-items: stretch;
          gap: 4mm;
          clear: both;
          margin: 6mm 0 0 0;
          break-inside: avoid;
          page-break-inside: avoid;
        }
        .liftup-fig-strip .liftup-fig {
          flex: 1 1 0;
          width: auto;
          max-width: none;
          display: block;
          float: none;
          margin: 0;
        }
        .liftup-fig-strip .liftup-fig.liftup-fig-captioned { display: block; float: none; margin: 0; }
        .liftup-fig-strip .liftup-fig img { width: 100%; height: 100%; object-fit: contain; }
        """;

    /// <summary>Fertiges Style-Element für Dokumente, die diese Regeln noch nicht enthalten.</summary>
    public static string StyleElement => "<style>" + Css + "</style>";

    public string SizeToken => Size switch
    {
        ImageSize.Small => "small",
        ImageSize.Large => "large",
        _ => "medium",
    };

    public string PlacementToken => Placement switch
    {
        ImagePlacement.Left => "left",
        ImagePlacement.Right => "right",
        ImagePlacement.BelowText => "below",
        _ => "center",
    };

    public string FlowToken => Flow switch
    {
        ImageFlow.Square => "square",
        ImageFlow.Tight => "tight",
        ImageFlow.Through => "through",
        ImageFlow.TopAndBottom => "topbottom",
        _ => "inline",
    };

    /// <summary>Klassen am Bildrahmen; die Datenattribute wiederholen die Werte für den Editor.</summary>
    public string ClassList =>
        "liftup-fig liftup-fig-" + SizeToken + " liftup-fig-" + PlacementToken + " liftup-fig-" + FlowToken;

    /// <summary>
    /// Rahmt ein fertiges Bild-Tag als Figur mit Layout-Klassen, Daten und der
    /// optionalen Bildunterschrift.
    /// </summary>
    public static string Wrap(string imageHtml, ImageFormat? format = null)
    {
        if (string.IsNullOrWhiteSpace(imageHtml))
        {
            return string.Empty;
        }

        var value = format ?? Default;
        var caption = value.Caption?.Trim();
        var builder = new StringBuilder();
        builder.Append("<figure class=\"").Append(value.ClassList)
            .Append(string.IsNullOrEmpty(caption) ? string.Empty : " liftup-fig-captioned")
            .Append("\" data-liftup-size=\"").Append(value.SizeToken)
            .Append("\" data-liftup-place=\"").Append(value.PlacementToken)
            .Append("\" data-liftup-flow=\"").Append(value.FlowToken)
            .Append("\">")
            .Append(imageHtml);

        if (!string.IsNullOrEmpty(caption))
        {
            builder.Append("<figcaption>").Append(Encode(caption)).Append("</figcaption>");
        }

        builder.Append("</figure>");
        return builder.ToString();
    }

    /// <summary>Liest die Angaben, die der Editor beim Anklicken eines Bildes liefert.</summary>
    public static ImageFormat Parse(string? size, string? placement, string? flow, string? caption) => new(
        size switch
        {
            "small" => ImageSize.Small,
            "large" => ImageSize.Large,
            _ => ImageSize.Medium,
        },
        placement switch
        {
            "left" => ImagePlacement.Left,
            "right" => ImagePlacement.Right,
            "below" => ImagePlacement.BelowText,
            _ => ImagePlacement.Center,
        },
        flow switch
        {
            "square" => ImageFlow.Square,
            "tight" => ImageFlow.Tight,
            "through" => ImageFlow.Through,
            "topbottom" => ImageFlow.TopAndBottom,
            _ => ImageFlow.Inline,
        },
        string.IsNullOrWhiteSpace(caption) ? null : caption.Trim());

    private static string Encode(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
