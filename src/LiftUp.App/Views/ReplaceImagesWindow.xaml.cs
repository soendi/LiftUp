using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;
using Microsoft.Win32;

namespace LiftUp.App.Views;

/// <summary>
/// Ersetzt fehlende Bilder aus dem Vorlageneditor. Vorlagen hängen an keinem Projekt,
/// deshalb landen die Kopien in der globalen Bibliothek.
/// </summary>
public partial class ReplaceImagesWindow : Window
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

    private readonly AppServices _services;

    public ReplaceImagesWindow(AppServices services, IReadOnlyList<string> missing)
    {
        _services = services;

        InitializeComponent();

        Rows = missing.Select(name => new ImageRow { Name = name }).ToArray();
        RowsList.ItemsSource = Rows;
    }

    public IReadOnlyList<ImageRow> Rows { get; }

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
        var chosen = Rows.Where(row => row.FilePath is not null).ToArray();
        if (chosen.Length == 0)
        {
            HintText.Text = "Bitte zuerst mindestens eine Datei wählen.";
            return;
        }

        try
        {
            foreach (var row in chosen)
            {
                // Ohne Projekt: die Vorlage gilt für jedes Projekt, die Kopie gehört zur globalen Bibliothek.
                _services.Images.ReplaceFile(null, row.Name, row.FilePath!);
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

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
