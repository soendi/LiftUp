using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class SnippetsView : UserControl
{
    private readonly AppServices _services;
    private Guid? _snippetId;
    private bool _isArchived;

    /// <summary>Beim Neuladen der Liste den Editor nicht mit dem gespeicherten Stand überschreiben.</summary>
    private bool _refreshing;

    public SnippetsView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        // Markierter Text lässt sich hier ebenso als Textvorlage ablegen.
        BodyEditor.SnippetServices = services;
        // Textbausteine hängen an keinem Projekt; Bilder werden ohne Kopie eingesetzt.
        BodyEditor.EnableImageInsert(services);
        LoadSnippets();

        // Ein im Text-Editor gespeicherter Baustein steht sofort in der Liste.
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                RefreshList();
            }
        };
    }

    /// <summary>
    /// Liest die Liste neu, ohne den offenen Textbaustein zu ersetzen: Wer gerade tippt,
    /// verliert beim Reiterwechsel nichts; ein neuer Baustein erscheint trotzdem sofort.
    /// </summary>
    private void RefreshList()
    {
        try
        {
            var snippets = _services.Snippets.GetAll(includeArchived: true);

            _refreshing = true;
            try
            {
                SnippetsGrid.ItemsSource = snippets;
                SnippetsGrid.SelectedItem = snippets.FirstOrDefault(item => item.Id == _snippetId);
            }
            finally
            {
                _refreshing = false;
            }

            // Der offene Baustein wurde inzwischen anderswo gelöscht.
            if (_snippetId is { } id && snippets.All(item => item.Id != id))
            {
                ClearEditor();
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void LoadSnippets(Guid? selectId = null)
    {
        try
        {
            var snippets = _services.Snippets.GetAll(includeArchived: true);
            SnippetsGrid.ItemsSource = snippets;

            var target = selectId ?? _snippetId;
            var selected = snippets.FirstOrDefault(item => item.Id == target) ?? snippets.FirstOrDefault();
            SnippetsGrid.SelectedItem = selected;

            if (selected is null)
            {
                ClearEditor();
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }

        if (SnippetsGrid.SelectedItem is TextSnippetListItem item)
        {
            LoadSnippet(item.Id);
        }
    }

    private void LoadSnippet(Guid id)
    {
        try
        {
            var snippet = _services.Snippets.Get(id);
            _snippetId = snippet.Id;
            _isArchived = snippet.IsArchived;

            NameBox.Text = snippet.Name;
            CategoryBox.Text = snippet.Category ?? string.Empty;
            BodyEditor.SetHtml(snippet.Html);

            ArchiveButton.IsEnabled = true;
            DeleteButton.IsEnabled = true;
            ArchiveButton.Content = snippet.IsArchived ? "Wiederherstellen" : "Archivieren";
            InfoText.Text = $"Version {snippet.Version}, geändert {snippet.UpdatedUtc.ToLocalTime():dd.MM.yyyy HH:mm}";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void ClearEditor()
    {
        _snippetId = null;
        _isArchived = false;
        NameBox.Text = string.Empty;
        CategoryBox.Text = string.Empty;
        BodyEditor.SetHtml(string.Empty);
        ArchiveButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        ClearEditor();
        SnippetsGrid.SelectedItem = null;
        BodyEditor.SetHtml("<p>Text des Bausteins …</p>");
        NameBox.Focus();
        InfoText.Text = "Neuer Textbaustein – Namen vergeben und speichern.";
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string html;
        try
        {
            html = await BodyEditor.GetHtmlAsync();
        }
        catch (Exception exception)
        {
            InfoText.Text = "Der Text konnte nicht gelesen werden: " + exception.Message;
            return;
        }

        var input = new SnippetInput
        {
            Name = NameBox.Text,
            Category = CategoryBox.Text,
            Html = html,
        };

        try
        {
            TextSnippet snippet;
            if (_snippetId is null)
            {
                snippet = _services.Snippets.Create(input);
            }
            else
            {
                snippet = _services.Snippets.Update(_snippetId.Value, input, _isArchived);
            }

            _snippetId = snippet.Id;
            LoadSnippets(snippet.Id);
            InfoText.Text = $"Gespeichert: {snippet.Name} (Version {snippet.Version}).";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Speichern fehlgeschlagen: " + exception.Message;
        }
    }

    private async void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        if (_snippetId is null)
        {
            return;
        }

        string html;
        try
        {
            html = await BodyEditor.GetHtmlAsync();
        }
        catch (Exception exception)
        {
            InfoText.Text = "Der Text konnte nicht gelesen werden: " + exception.Message;
            return;
        }

        try
        {
            var input = new SnippetInput
            {
                Name = NameBox.Text,
                Category = CategoryBox.Text,
                Html = html,
            };

            var snippet = _services.Snippets.Update(_snippetId.Value, input, !_isArchived);
            _isArchived = snippet.IsArchived;
            LoadSnippets(snippet.Id);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_snippetId is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Textbaustein \"{NameBox.Text}\" wirklich löschen?\n\n" +
            "Der Baustein wird endgültig entfernt. Bereits erzeugte PDFs behalten ihren Text.",
            "Textbaustein löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Snippets.Delete(_snippetId.Value);
            _snippetId = null;
            _isArchived = false;
            LoadSnippets();
            InfoText.Text = "Textbaustein gelöscht.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Löschen fehlgeschlagen: " + exception.Message;
        }
    }
}
