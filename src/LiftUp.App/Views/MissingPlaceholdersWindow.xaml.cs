using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;
using Microsoft.Win32;

namespace LiftUp.App.Views;

/// <summary>
/// Repariert fehlende Angaben einer Vorlage: Bilder lassen sich je Zeile durch eine Datei
/// ersetzen, die unter demselben Namen übernommen wird; fehlende Bilder und Textbausteine
/// können alternativ als Platzhalter aus der Vorlage entfernt werden.
/// </summary>
public partial class MissingPlaceholdersWindow : Window
{
    public sealed class ImageRow : INotifyPropertyChanged
    {
        private string? _filePath;

        public required string Name { get; init; }

        public string? FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                OnPropertyChanged(nameof(FilePath));
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(ButtonText));
            }
        }

        public string Status => FilePath is null ? "fehlt" : Path.GetFileName(FilePath);

        public string ButtonText => FilePath is null ? "Datei wählen …" : "Andere Datei …";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class SnippetRow
    {
        public required string Name { get; init; }
    }

    private readonly AppServices _services;
    private readonly Guid _projectId;
    private readonly Guid _templateId;
    private readonly ObservableCollection<SnippetRow> _snippetRows;

    public MissingPlaceholdersWindow(
        AppServices services,
        Guid projectId,
        Guid templateId,
        MissingPlaceholders missing)
    {
        _services = services;
        _projectId = projectId;
        _templateId = templateId;

        InitializeComponent();

        ImageRows = missing.Images.Select(name => new ImageRow { Name = name }).ToArray();
        _snippetRows = new ObservableCollection<SnippetRow>(
            missing.Snippets.Select(name => new SnippetRow { Name = name }));

        ImagesList.ItemsSource = ImageRows;
        SnippetsList.ItemsSource = _snippetRows;

        ImagesHeader.Visibility = ImageRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ImagesList.Visibility = ImagesHeader.Visibility;
        RefreshSnippetSection();

        // Ohne Bilder gibt es nichts zu ersetzen – dann bleibt nur das Entfernen.
        ApplyButton.Visibility = ImageRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public IReadOnlyList<ImageRow> ImageRows { get; }

    /// <summary>Wahr, wenn die Platzhalter aus der Vorlage entfernt wurden.</summary>
    public bool PlaceholdersRemoved { get; private set; }

    /// <summary>Wahr, wenn mindestens ein fehlender Textbaustein angelegt wurde.</summary>
    public bool SnippetsCreated { get; private set; }

    private void RefreshSnippetSection()
    {
        var visible = _snippetRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SnippetsHeader.Visibility = visible;
        SnippetsList.Visibility = visible;
        SnippetsHint.Visibility = visible;
    }

    private void OnCreateSnippetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SnippetRow row })
        {
            return;
        }

        var dialog = new SnippetCreateWindow(_services, row.Name) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SnippetsCreated = true;
        _snippetRows.Remove(row);
        RefreshSnippetSection();
        HintText.Text = $"Textbaustein „{row.Name}“ angelegt.";

        // Nichts mehr zu tun: Dialog schließen, damit das PDF erneut erzeugt wird.
        if (_snippetRows.Count == 0 && ImageRows.Count == 0)
        {
            DialogResult = true;
        }
    }

    private void OnChooseFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ImageRow row })
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Bild für \"{row.Name}\" wählen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.svg|Alle Dateien|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            row.FilePath = dialog.FileName;
            HintText.Text = string.Empty;
        }
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        var chosen = ImageRows.Where(row => row.FilePath is not null).ToArray();
        if (chosen.Length == 0)
        {
            HintText.Text = "Bitte zuerst mindestens eine Datei wählen oder die Platzhalter entfernen.";
            return;
        }

        try
        {
            foreach (var row in chosen)
            {
                _services.Images.ReplaceFile(_projectId, row.Name, row.FilePath!);
            }

            DialogResult = true;
        }
        catch (LiftUpException exception)
        {
            HintText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            HintText.Text = "Ersetzen fehlgeschlagen: " + exception.Message;
        }
    }

    private void OnRemovePlaceholdersClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            FehlendeAngaben()
            + " als Platzhalter aus der Vorlage entfernen?\n\n"
            + "Die Vorlage erhält dafür eine neue Version; bereits erzeugte PDFs bleiben unverändert.",
            "Platzhalter entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var row in ImageRows)
            {
                _services.Templates.RemoveImagePlaceholder(_templateId, row.Name);
            }

            foreach (var row in _snippetRows.ToArray())
            {
                _services.Templates.RemoveSnippetPlaceholder(_templateId, row.Name);
            }

            PlaceholdersRemoved = true;
            DialogResult = true;
        }
        catch (LiftUpException exception)
        {
            HintText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            HintText.Text = "Platzhalter konnten nicht entfernt werden: " + exception.Message;
        }
    }

    /// <summary>„2 fehlende Bilder und 1 fehlender Textbaustein“ für die Rückfrage.</summary>
    private string FehlendeAngaben()    {
        var parts = new List<string>();
        if (ImageRows.Count > 0)
        {
            parts.Add($"{ImageRows.Count} fehlende{(ImageRows.Count == 1 ? "s" : "")} Bild{(ImageRows.Count == 1 ? "" : "er")}");
        }

        if (_snippetRows.Count > 0)
        {
            parts.Add(
                $"{_snippetRows.Count} fehlende{(_snippetRows.Count == 1 ? "r" : "")} Textbaustein{(_snippetRows.Count == 1 ? "" : "e")}");
        }

        var text = string.Join(" und ", parts);
        return text.Length == 0 ? "Alle Platzhalter" : char.ToUpper(text[0]) + text[1..];
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
