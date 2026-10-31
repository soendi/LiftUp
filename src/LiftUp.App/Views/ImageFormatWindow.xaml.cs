using System.Windows;
using LiftUp.Core.Documents;

namespace LiftUp.App.Views;

/// <summary>
/// Fragt ab, wie ein Bild im Text stehen soll: Größe, Seite, Textfluss und eine
/// optionale Bildunterschrift. Die aktuellen Angaben sind vorgewählt.
/// </summary>
public partial class ImageFormatWindow : Window
{
    public ImageFormatWindow(ImageFormat format)
    {
        InitializeComponent();

        SizeSmall.IsChecked = format.Size == ImageSize.Small;
        SizeMedium.IsChecked = format.Size == ImageSize.Medium;
        SizeLarge.IsChecked = format.Size == ImageSize.Large;

        PlaceLeft.IsChecked = format.Placement == ImagePlacement.Left;
        PlaceCenter.IsChecked = format.Placement == ImagePlacement.Center;
        PlaceRight.IsChecked = format.Placement == ImagePlacement.Right;
        PlaceBelow.IsChecked = format.Placement == ImagePlacement.BelowText;

        FlowInline.IsChecked = format.Flow == ImageFlow.Inline;
        FlowSquare.IsChecked = format.Flow == ImageFlow.Square;
        FlowTight.IsChecked = format.Flow == ImageFlow.Tight;
        FlowThrough.IsChecked = format.Flow == ImageFlow.Through;
        FlowTopBottom.IsChecked = format.Flow == ImageFlow.TopAndBottom;

        CaptionBox.Text = format.Caption ?? string.Empty;

        Loaded += (_, _) =>
        {
            CaptionBox.Focus();
            CaptionBox.SelectAll();
        };
    }

    /// <summary>Die im Dialog gewählten Angaben.</summary>
    public ImageFormat Format => new(
        SizeSmall.IsChecked == true ? ImageSize.Small
            : SizeLarge.IsChecked == true ? ImageSize.Large
            : ImageSize.Medium,
        PlaceLeft.IsChecked == true ? ImagePlacement.Left
            : PlaceRight.IsChecked == true ? ImagePlacement.Right
            : PlaceBelow.IsChecked == true ? ImagePlacement.BelowText
            : ImagePlacement.Center,
        FlowSquare.IsChecked == true ? ImageFlow.Square
            : FlowTight.IsChecked == true ? ImageFlow.Tight
            : FlowThrough.IsChecked == true ? ImageFlow.Through
            : FlowTopBottom.IsChecked == true ? ImageFlow.TopAndBottom
            : ImageFlow.Inline,
        string.IsNullOrWhiteSpace(CaptionBox.Text) ? null : CaptionBox.Text.Trim());

    private void OnApplyClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
