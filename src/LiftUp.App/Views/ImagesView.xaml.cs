using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LiftUp.Core.Domain;
using Microsoft.Win32;

namespace LiftUp.App.Views;

public partial class ImagesView : UserControl
{
    private sealed record ProjectOption(Guid? Id, string Name);

    private const string AllCategories = "Alle Kategorien";

    private readonly AppServices _services;
    private IReadOnlyList<ImageListItem> _items = [];

    public ImagesView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        LoadProjects();
        LoadImages();
    }

    private void LoadProjects()
    {
        var options = new List<ProjectOption> { new(null, "Globale Bibliothek") };

        try
        {
            options.AddRange(_services.Projects.Search(new ProjectQuery())
                .Select(project => new ProjectOption(project.Id, $"{project.Number} · {project.Name}")));
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }

        ProjectBox.ItemsSource = options;
        ProjectBox.SelectedIndex = 0;
    }

    private Guid? SelectedProjectId => (ProjectBox.SelectedItem as ProjectOption)?.Id;

    private void LoadImages()
    {
        try
        {
            _items = _services.Images.Search(SelectedProjectId, SearchBox.Text, ArchiveToggle.IsChecked == true);

            var categories = new List<string> { AllCategories };
            categories.AddRange(_items
                .Select(item => item.Category)
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase));

            CategoryFilterBox.ItemsSource = categories;
            CategoryFilterBox.SelectedIndex = 0;

            ApplyCategoryFilter();
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void ApplyCategoryFilter()
    {
        var category = CategoryFilterBox.SelectedItem as string;
        var filtered = string.IsNullOrWhiteSpace(category) || category == AllCategories
            ? _items
            : _items.Where(item => string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase)).ToArray();

        ImagesGrid.ItemsSource = filtered;
        InfoText.Text = filtered.Count == 1 ? "1 Bild" : $"{filtered.Count} Bilder";
    }

    private ImageListItem? Selected => ImagesGrid.SelectedItem as ImageListItem;

    private void OnFilterChanged(object sender, RoutedEventArgs e) => LoadImages();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => LoadImages();

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e) => ApplyCategoryFilter();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => LoadImages();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = Selected;
        ArchiveButton.IsEnabled = selected is not null;
        DeleteButton.IsEnabled = selected is not null;
        ArchiveButton.Content = selected?.IsArchived == true ? "Wiederherstellen" : "Archivieren";

        PreviewImage.Source = null;
        PlaceholderText.Text = string.Empty;

        if (selected is null || !File.Exists(selected.FilePath))
        {
            return;
        }

        PlaceholderText.Text = selected.Placeholder;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(selected.FilePath);
            bitmap.EndInit();
            PreviewImage.Source = bitmap;
        }
        catch (Exception exception)
        {
            PlaceholderText.Text = "Vorschau nicht möglich: " + exception.Message;
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Bild auswählen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.svg;*.tif;*.tiff|Alle Dateien|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var input = new ImageInput
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text)
                ? Path.GetFileNameWithoutExtension(dialog.FileName)
                : NameBox.Text,
            Category = ImportCategoryBox.Text,
            ProjectId = SelectedProjectId,
        };

        try
        {
            var image = _services.Images.Import(dialog.FileName, input);
            NameBox.Text = string.Empty;
            LoadImages();
            InfoText.Text = $"Importiert: {image.Name} ({image.SizeText})";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Import fehlgeschlagen: " + exception.Message;
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
            _services.Images.SetArchived(selected.Id, !selected.IsArchived);
            LoadImages();
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Bild \"{selected.Name}\" wirklich löschen?\n\n" +
            "Der Eintrag und die Kopie im Bibliotheksordner werden entfernt. " +
            "Bereits erzeugte PDFs behalten das eingebettete Bild.",
            "Bild löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var warning = _services.Images.Delete(selected.Id);
            LoadImages();
            InfoText.Text = warning ?? $"Gelöscht: {selected.Name}";

            if (warning is not null)
            {
                MessageBox.Show(warning, "Bild löschen", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }
}
