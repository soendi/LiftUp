using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LiftUp.App.Views;

/// <summary>Füllstand eines Reiters: nichts erfasst, teilweise erfasst oder alles erfasst.</summary>
public enum TabFill
{
    /// <summary>Nichts erfasst – der Reiter ist hellrot.</summary>
    None,

    /// <summary>Etwas fehlt noch – der Reiter ist hellgelb.</summary>
    Partial,

    /// <summary>Alles erfasst – der Reiter ist hellgrün.</summary>
    Full,
}

/// <summary>
/// Ein Reiter, der seinen Füllstand meldet – Grundlage für die Farbe des Haupt-Reiters im
/// Fenster (Tabelle „Zustandsanalyse“, „Investitionsplanung“, „Analyse“).
/// </summary>
public interface ITabFillSource
{
    TabFill Fill { get; }

    event EventHandler? FillChanged;
}

/// <summary>
/// Farbe eines Reiters: hellrot ohne Inhalt, hellgelb solange etwas fehlt, hellgrün sobald
/// alles erfasst ist. Die Beschriftung bleibt ein normaler Reitertext, die Farbe sitzt dahinter.
/// </summary>
public static class TabHeaderStyle
{
    private static readonly SolidColorBrush MissingBrush = Create(0xF9, 0xD6, 0xD5);
    private static readonly SolidColorBrush PartialBrush = Create(0xFA, 0xEC, 0xC0);
    private static readonly SolidColorBrush DoneBrush = Create(0xD7, 0xF0, 0xDA);

    /// <summary>Setzt die Beschriftung samt Farbe an den Reiter.</summary>
    public static void Apply(TabItem tab, string header, TabFill fill)
    {
        ArgumentNullException.ThrowIfNull(tab);

        tab.Header = new Border
        {
            Background = fill switch
            {
                TabFill.Full => DoneBrush,
                TabFill.Partial => PartialBrush,
                _ => MissingBrush,
            },
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 1, 7, 1),
            Child = new TextBlock
            {
                Text = header,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static SolidColorBrush Create(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
