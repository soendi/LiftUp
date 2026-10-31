using System.Windows;
using System.Windows.Input;

namespace LiftUp.App.Views;

/// <summary>Kleine Auswahlliste für Textbausteine und Bilder.</summary>
public partial class PickerWindow : Window
{
    public sealed record Item(Guid Id, string Name, string Detail);

    public PickerWindow(string heading, IEnumerable<Item> items, string confirmText = "Einfügen")
    {
        InitializeComponent();

        HeadingText.Text = heading;
        ItemList.ItemsSource = items.ToArray();
        if (ItemList.Items.Count > 0)
        {
            ItemList.SelectedIndex = 0;
        }

        ConfirmButton.Content = confirmText;
    }

    public Item? Selected => ItemList.SelectedItem as Item;

    private void OnInsertClick(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
        {
            return;
        }

        DialogResult = true;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null)
        {
            DialogResult = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
