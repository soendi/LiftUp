using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class DocumentTypesWindow : Window
{
    private readonly AppServices _services;

    public DocumentTypesWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        LoadTypes();
    }

    private void LoadTypes()
    {
        try
        {
            TypesGrid.ItemsSource = _services.DocumentTypes.GetAll(includeArchived: true);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private DocumentType? Selected => TypesGrid.SelectedItem as DocumentType;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = Selected;
        NameBox.Text = selected?.Name ?? string.Empty;
        ArchiveButton.IsEnabled = selected is not null;
        ArchiveButton.Content = selected?.IsArchived == true ? "Wiederherstellen" : "Archivieren";
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var type = _services.DocumentTypes.Create(new DocumentTypeInput { Name = NameBox.Text });
            NameBox.Text = string.Empty;
            LoadTypes();
            InfoText.Text = $"Angelegt: {type.Name}";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            InfoText.Text = "Bitte zuerst einen Typ auswählen.";
            return;
        }

        try
        {
            var renamed = _services.DocumentTypes.Rename(selected.Id, NameBox.Text);
            LoadTypes();
            InfoText.Text = $"Umbenannt in {renamed.Name}. Vorlagen wurden mitgezogen.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            return;
        }

        try
        {
            _services.DocumentTypes.SetArchived(selected.Id, !selected.IsArchived);
            LoadTypes();
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
