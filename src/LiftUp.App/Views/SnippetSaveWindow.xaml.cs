using System.Windows;
using LiftUp.Core.Documents;

namespace LiftUp.App.Views;

/// <summary>
/// Nimmt den im Dokument markierten Text als neuen Textbaustein auf. Der Inhalt
/// wird bereits mit Formatierung übergeben; hier werden nur Name und Kategorie
/// abgefragt.
/// </summary>
public partial class SnippetSaveWindow : Window
{
    public SnippetSaveWindow(string html)
    {
        InitializeComponent();

        PreviewText.Text = SnippetAutocomplete.Preview(html, 2000);
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string SnippetName => NameBox.Text.Trim();

    public string? Category => string.IsNullOrWhiteSpace(CategoryBox.Text)
        ? null
        : CategoryBox.Text.Trim();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (SnippetName.Length == 0)
        {
            HintText.Text = "Bitte einen Namen für den Textbaustein angeben.";
            NameBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
