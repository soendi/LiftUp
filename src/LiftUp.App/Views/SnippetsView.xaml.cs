using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class SnippetsView : UserControl
{
    private readonly AppServices _services;
    private Guid? _snippetId;
    private bool _isArchived;

    public SnippetsView(AppServices services)
    {
        _services = services;
        InitializeComponent();
        LoadSnippets();
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
            HtmlBox.Text = snippet.Html;

            ArchiveButton.IsEnabled = true;
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
        HtmlBox.Text = string.Empty;
        ArchiveButton.IsEnabled = false;
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        ClearEditor();
        SnippetsGrid.SelectedItem = null;
        HtmlBox.Text = "<p>Text des Bausteins …</p>";
        NameBox.Focus();
        InfoText.Text = "Neuer Textbaustein – Namen vergeben und speichern.";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var input = new SnippetInput
        {
            Name = NameBox.Text,
            Category = CategoryBox.Text,
            Html = HtmlBox.Text,
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

    private void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        if (_snippetId is null)
        {
            return;
        }

        try
        {
            var input = new SnippetInput
            {
                Name = NameBox.Text,
                Category = CategoryBox.Text,
                Html = HtmlBox.Text,
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
}
