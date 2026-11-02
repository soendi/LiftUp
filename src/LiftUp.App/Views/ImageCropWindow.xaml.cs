using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace LiftUp.App.Views;

/// <summary>
/// Schneidet ein Foto zu: Im Bild mit der Maus einen Ausschnitt aufziehen, „Übernehmen“
/// übernimmt genau diesen Bereich. Ohne Auswahl bleibt das ganze Foto.
/// </summary>
public partial class ImageCropWindow : Window
{
    /// <summary>Kleinste Kantenlänge in Bildpixeln, damit kein unbrauchbarer Rest entsteht.</summary>
    private const int MinimumPixels = 40;

    private readonly BitmapSource _source;
    private double _scale = 1;
    private bool _dragging;
    private Point _start;
    private Int32Rect? _selection;

    public ImageCropWindow(string filePath)
    {
        InitializeComponent();

        _source = LoadBitmap(filePath);
        Photo.Source = _source;

        Stage.SizeChanged += (_, _) => Layout();
        Overlay.MouseLeftButtonDown += OnMouseDown;
        Overlay.MouseMove += OnMouseMove;
        Overlay.MouseLeftButtonUp += OnMouseUp;
        Loaded += (_, _) => Layout();
    }

    /// <summary>Ausschnitt in Bildpixeln; gesetzt, wenn der Dialog mit „Übernehmen“ endet.</summary>
    public BitmapSource? Result { get; private set; }

    /// <summary>Schreibt ein Bild als PNG an die angegebene Stelle.</summary>
    public static void SavePng(BitmapSource image, string filePath)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }

    private static BitmapSource LoadBitmap(string filePath)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(filePath);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Setzt Bild und Auswahlfläche mittig in die Bühne und zeichnet den Ausschnitt neu.</summary>
    private void Layout()
    {
        if (_source.PixelWidth <= 0 || _source.PixelHeight <= 0 ||
            Stage.ActualWidth <= 0 || Stage.ActualHeight <= 0)
        {
            return;
        }

        _scale = Math.Min(Stage.ActualWidth / _source.PixelWidth, Stage.ActualHeight / _source.PixelHeight);
        var width = _source.PixelWidth * _scale;
        var height = _source.PixelHeight * _scale;

        foreach (var element in new FrameworkElement[] { Photo, Overlay })
        {
            element.Width = width;
            element.Height = height;
            element.Margin = new Thickness(
                Math.Max(0, (Stage.ActualWidth - width) / 2),
                Math.Max(0, (Stage.ActualHeight - height) / 2),
                0,
                0);
        }

        DrawSelection();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_scale <= 0)
        {
            return;
        }

        _dragging = true;
        _start = Clamp(e.GetPosition(Overlay));
        Overlay.CaptureMouse();
        UpdateSelection(_start, _start);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        UpdateSelection(_start, Clamp(e.GetPosition(Overlay)));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Overlay.ReleaseMouseCapture();
        UpdateSelection(_start, Clamp(e.GetPosition(Overlay)));
    }

    /// <summary>Hält den Zeiger im Bild fest, damit der Ausschnitt nicht daneben liegt.</summary>
    private Point Clamp(Point point) => new(
        Math.Clamp(point.X, 0, Overlay.ActualWidth),
        Math.Clamp(point.Y, 0, Overlay.ActualHeight));

    /// <summary>Merkt sich den aufgezogenen Bereich in Bildpixeln und zeichnet ihn ein.</summary>
    private void UpdateSelection(Point from, Point to)
    {
        var left = Math.Min(from.X, to.X);
        var top = Math.Min(from.Y, to.Y);
        var width = Math.Abs(to.X - from.X);
        var height = Math.Abs(to.Y - from.Y);

        if (width < 6 || height < 6)
        {
            _selection = null;
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionRect.Visibility = Visibility.Visible;
        SelectionRect.Width = width;
        SelectionRect.Height = height;
        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);

        var x = (int)Math.Round(left / _scale);
        var y = (int)Math.Round(top / _scale);
        var pixelWidth = (int)Math.Round(width / _scale);
        var pixelHeight = (int)Math.Round(height / _scale);

        x = Math.Clamp(x, 0, Math.Max(0, _source.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, _source.PixelHeight - 1));
        pixelWidth = Math.Clamp(pixelWidth, 1, _source.PixelWidth - x);
        pixelHeight = Math.Clamp(pixelHeight, 1, _source.PixelHeight - y);

        _selection = new Int32Rect(x, y, pixelWidth, pixelHeight);
    }

    /// <summary>Zeichnet den gemerkten Ausschnitt – etwa nach einer Größenänderung des Fensters.</summary>
    private void DrawSelection()
    {
        if (_selection is not { } selection || _scale <= 0)
        {
            return;
        }

        SelectionRect.Visibility = Visibility.Visible;
        SelectionRect.Width = selection.Width * _scale;
        SelectionRect.Height = selection.Height * _scale;
        Canvas.SetLeft(SelectionRect, selection.X * _scale);
        Canvas.SetTop(SelectionRect, selection.Y * _scale);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var rect = _selection ?? new Int32Rect(0, 0, _source.PixelWidth, _source.PixelHeight);
        if (rect.Width < MinimumPixels || rect.Height < MinimumPixels)
        {
            HintText.Text = "Der Ausschnitt ist zu klein. Bitte einen größeren Bereich aufziehen.";
            return;
        }

        var cropped = new CroppedBitmap(_source, rect);
        cropped.Freeze();

        Result = cropped;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
