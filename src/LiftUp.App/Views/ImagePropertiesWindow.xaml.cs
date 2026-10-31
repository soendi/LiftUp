using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>
/// Erfasst Name, Kategorie und Schlagworte mehrerer Bilder vor dem Import und
/// dieselben Angaben für ein bereits vorhandenes Bild.
/// </summary>
public partial class ImagePropertiesWindow : Window
{
    public sealed class Row
    {
        public string FilePath { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Tags { get; set; } = string.Empty;
    }

    public ImagePropertiesWindow(IEnumerable<string> filePaths, Guid? projectId, string? defaultCategory = null)
    {
        InitializeComponent();

        var category = defaultCategory?.Trim() ?? string.Empty;
        Rows = filePaths
            .Where(File.Exists)
            .Select(path => new Row
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Name = Path.GetFileNameWithoutExtension(path),
                Category = category,
            })
            .ToList();

        RowsGrid.ItemsSource = Rows;
        Loaded += (_, _) =>
        {
            if (RowsGrid.Items.Count > 0)
            {
                RowsGrid.SelectedIndex = 0;
                RowsGrid.Focus();
            }
        };
    }

    public ImagePropertiesWindow(LibraryImage image)
    {
        InitializeComponent();

        Title = "Bild bearbeiten";
        HeadingText.Text = "Bild bearbeiten";
        ConfirmButton.Content = "Speichern";

        Rows =
        [
            new Row
            {
                FilePath = image.FilePath,
                FileName = image.FileName,
                Name = image.Name,
                Category = image.Category ?? string.Empty,
                Tags = image.Tags ?? string.Empty,
            },
        ];

        RowsGrid.ItemsSource = Rows;
        Loaded += (_, _) =>
        {
            RowsGrid.SelectedIndex = 0;
            RowsGrid.Focus();
        };
    }

    public IReadOnlyList<Row> Rows { get; }

    public Row? Single => Rows.Count == 1 ? Rows[0] : null;

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        // Eine noch offene Zelle muss übernommen werden, sonst fehlt der letzte Eintrag.
        RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        foreach (var row in Rows)
        {
            row.Name = (row.Name ?? string.Empty).Trim();
            if (row.Name.Length == 0)
            {
                HintText.Text = $"Bitte für \"{row.FileName}\" einen Namen angeben.";
                return;
            }
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
