using System.Windows;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>
/// Legt einen Textbaustein an, dessen Platzhalter in einer Vorlage verwendet wird, aber
/// nicht mehr existiert. Der Name ist vorgegeben, damit <c>{{snippet:Name}}</c> danach passt.
/// </summary>
public partial class SnippetCreateWindow : Window
{
    private readonly AppServices _services;

    public SnippetCreateWindow(AppServices services, string name)
    {
        _services = services;

        InitializeComponent();

        NameBox.Text = name;
        BodyEditor.SetHtml("<p></p>");
    }

    public TextSnippet? Created { get; private set; }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        string html;
        try
        {
            html = await BodyEditor.GetHtmlAsync();
        }
        catch (Exception exception)
        {
            HintText.Text = "Der Text konnte nicht gelesen werden: " + exception.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(html) || html.Trim() is "<p></p>" or "<p><br></p>")
        {
            HintText.Text = "Bitte den Text des Bausteins erfassen.";
            return;
        }

        try
        {
            Created = _services.Snippets.Create(new SnippetInput
            {
                Name = NameBox.Text,
                Category = CategoryBox.Text,
                Html = html,
            });

            DialogResult = true;
        }
        catch (LiftUpException exception)
        {
            HintText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            HintText.Text = "Anlegen fehlgeschlagen: " + exception.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
