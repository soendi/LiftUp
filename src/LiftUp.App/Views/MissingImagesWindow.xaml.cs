using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;
using Microsoft.Win32;

namespace LiftUp.App.Views;

/// <summary>
/// Repariert fehlende Bilder einer Vorlage: entweder je Bild eine Datei auswählen, die
/// unter demselben Namen übernommen wird, oder die Platzhalter aus der Vorlage entfernen.
/// </summary>
public partial class MissingImagesWindow : Window
{
    public sealed class MissingRow : INotifyPropertyChanged
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

    private readonly AppServices _services;
    private readonly Guid _projectId;
    private readonly Guid _templateId;

    public MissingImagesWindow(AppServices services, Guid projectId, Guid templateId, IReadOnlyList<string> missing)
    {
        _services = services;
        _projectId = projectId;
        _templateId = templateId;

        InitializeComponent();

        Rows = missing.Select(name => new MissingRow { Name = name }).ToArray();
        RowsList.ItemsSource = Rows;
    }

    public IReadOnlyList<MissingRow> Rows { get; }

    /// <summary>Wahr, wenn die Platzhalter aus der Vorlage entfernt wurden.</summary>
    public bool PlaceholdersRemoved { get; private set; }

    private void OnChooseFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MissingRow row })
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
        var chosen = Rows.Where(row => row.FilePath is not null).ToArray();
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
            $"Platzhalter für {Rows.Count} fehlende{(Rows.Count == 1 ? "s" : "")} Bild"
            + $"{(Rows.Count == 1 ? "" : "er")} aus der Vorlage entfernen?\n\n"
            + "Die Vorlage erhält dafür eine neue Version; bereits erzeugte PDFs bleiben unverändert.",
            "Platzhalter entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var row in Rows)
            {
                _services.Templates.RemoveImagePlaceholder(_templateId, row.Name);
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

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
