using System.Windows;

namespace LiftUp.App.Views;

/// <summary>
/// Vergleichsfenster: links der eigene Text, rechts der Vorschlag der KI. Erst „Text
/// übernehmen“ ersetzt den Text im Editor, „Abbrechen“ lässt ihn unverändert.
/// </summary>
public partial class AiTextWindow : Window
{
    public AiTextWindow(string actionLabel, string userText, string aiText)
    {
        InitializeComponent();

        Title = "KI-Vorschlag – " + actionLabel;
        HeadingText.Text = actionLabel;
        UserBox.Text = userText;
        AiBox.Text = aiText;
        AiBox.Focus();
        AiBox.CaretIndex = AiBox.Text.Length;
    }

    /// <summary>Der übernommene Text; gesetzt, wenn das Fenster mit „Text übernehmen“ schließt.</summary>
    public string Result { get; private set; } = string.Empty;

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        var text = AiBox.Text.Trim();
        if (text.Length == 0)
        {
            MessageBox.Show(this, "Der Vorschlag der KI ist leer – es gibt nichts zu übernehmen.",
                "KI-Vorschlag", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Result = text;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
