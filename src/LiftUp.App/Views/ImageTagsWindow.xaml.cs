using System.Windows;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>
/// Ändert die Schlagworte mehrerer markierter Bilder auf einmal: Schlagworte lassen
/// sich hinzufügen und entfernen; vorhandene Vorschläge erleichtern die Eingabe.
/// </summary>
public partial class ImageTagsWindow : Window
{
    public ImageTagsWindow(IReadOnlyList<ImageListItem> images, IReadOnlyList<string> knownTags)
    {
        InitializeComponent();

        var names = images.Select(image => image.Name).ToArray();
        CountText.Text = names.Length == 1
            ? $"Bild: {names[0]}"
            : $"{names.Length} Bilder: {string.Join(", ", names.Take(6))}"
              + (names.Length > 6 ? " …" : string.Empty);

        AddBox.ItemsSource = knownTags;
        RemoveBox.ItemsSource = images
            .SelectMany(image => ImageTags.Split(image.Tags))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        Loaded += (_, _) => AddBox.Focus();
    }

    public string? AddTags => string.IsNullOrWhiteSpace(AddBox.Text) ? null : AddBox.Text;

    public string? RemoveTags => string.IsNullOrWhiteSpace(RemoveBox.Text) ? null : RemoveBox.Text;

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AddBox.Text) && string.IsNullOrWhiteSpace(RemoveBox.Text))
        {
            HintText.Text = "Bitte mindestens ein Schlagwort zum Hinzufügen oder Entfernen angeben.";
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
