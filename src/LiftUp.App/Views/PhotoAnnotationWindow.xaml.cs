using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>
/// Kleiner Bild-Editor: Kreis oder Pfeil über ein Foto ziehen, um den Kunden auf eine
/// Stelle hinzuweisen. Gespeichert werden Bildpixel, damit das PDF dieselbe Markierung
/// an derselben Stelle zeigt.
/// </summary>
public partial class PhotoAnnotationWindow : Window
{
    private static readonly SolidColorBrush MarkColor = new(Color.FromRgb(0xE1, 0x1D, 0x48));

    private readonly List<PhotoAnnotation> _annotations = [];
    private readonly double _pixelWidth;
    private readonly double _pixelHeight;

    private Point? _start;
    private double _scale = 1;

    public PhotoAnnotationWindow(string filePath, IReadOnlyList<PhotoAnnotation>? annotations = null)
    {
        InitializeComponent();

        (_pixelWidth, _pixelHeight) = ReadPixelSize(filePath);
        Photo.Source = LoadImage(filePath);

        foreach (var annotation in annotations ?? [])
        {
            _annotations.Add(Clone(annotation));
        }

        CircleTool.IsChecked = true;

        if (Photo.Source is null || _pixelWidth <= 0 || _pixelHeight <= 0)
        {
            HintText.Text = "Dieses Bild lässt sich nicht darstellen. Für Markierungen bitte PNG oder JPG verwenden.";
            CircleTool.IsEnabled = false;
            ArrowTool.IsEnabled = false;
        }

        Stage.SizeChanged += (_, _) => Layout();
        Overlay.MouseLeftButtonDown += OnStageMouseDown;
        Overlay.MouseMove += OnStageMouseMove;
        Overlay.MouseLeftButtonUp += OnStageMouseUp;
        Loaded += (_, _) => Layout();
    }

    /// <summary>Markierungen für das Foto; gültig, wenn der Dialog mit „Übernehmen“ endet.</summary>
    public IReadOnlyList<PhotoAnnotation> Annotations => _annotations;

    /// <summary>Setzt Bild und Zeichenfläche mittig in die Bühne und zeichnet neu.</summary>
    private void Layout()
    {
        if (_pixelWidth <= 0 || _pixelHeight <= 0 ||
            Stage.ActualWidth <= 0 || Stage.ActualHeight <= 0)
        {
            return;
        }

        _scale = Math.Min(Stage.ActualWidth / _pixelWidth, Stage.ActualHeight / _pixelHeight);
        var width = _pixelWidth * _scale;
        var height = _pixelHeight * _scale;

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

        Redraw();
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        // Immer nur ein Werkzeug: der zuletzt geklickte Knopf bleibt gedrückt.
        var circle = ReferenceEquals(sender, CircleTool);
        CircleTool.IsChecked = circle;
        ArrowTool.IsChecked = !circle;
    }

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_pixelWidth <= 0)
        {
            return;
        }

        _start = e.GetPosition(Overlay);
        Overlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } start)
        {
            return;
        }

        Redraw(Preview(start, e.GetPosition(Overlay)));
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start)
        {
            return;
        }

        var end = e.GetPosition(Overlay);
        _start = null;
        Overlay.ReleaseMouseCapture();
        e.Handled = true;

        var annotation = Build(start, end);
        if (annotation is null)
        {
            Redraw();
            return;
        }

        _annotations.Add(annotation);
        Redraw();
    }

    /// <summary>Markierung aus zwei Punkten der Zeichenfläche; zu klein heißt: nichts zeichnen.</summary>
    private PhotoAnnotation? Build(Point start, Point end)
    {
        if (CircleTool.IsChecked == true)
        {
            var radius = Distance(start, end) / _scale;
            if (radius < 4)
            {
                return null;
            }

            return new PhotoAnnotation
            {
                Kind = PhotoAnnotationKind.Circle,
                X1 = start.X / _scale,
                Y1 = start.Y / _scale,
                Radius = radius,
            };
        }

        if (Distance(start, end) / _scale < 6)
        {
            return null;
        }

        return new PhotoAnnotation
        {
            Kind = PhotoAnnotationKind.Arrow,
            X1 = start.X / _scale,
            Y1 = start.Y / _scale,
            X2 = end.X / _scale,
            Y2 = end.Y / _scale,
        };
    }

    /// <summary>Markierung während des Ziehens; Radius und Pfeilspitze wachsen mit dem Zug.</summary>
    private PhotoAnnotation? Preview(Point start, Point end)
    {
        if (ArrowTool.IsChecked == true)
        {
            return new PhotoAnnotation
            {
                Kind = PhotoAnnotationKind.Arrow,
                X1 = start.X / _scale,
                Y1 = start.Y / _scale,
                X2 = end.X / _scale,
                Y2 = end.Y / _scale,
            };
        }

        var radius = Distance(start, end);
        return new PhotoAnnotation
        {
            Kind = PhotoAnnotationKind.Circle,
            X1 = start.X / _scale,
            Y1 = start.Y / _scale,
            Radius = radius / _scale,
        };
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));

    /// <summary>Zeichnet alle Markierungen neu, optional mit der gerade gezogenen.</summary>
    private void Redraw(PhotoAnnotation? preview = null)
    {
        Overlay.Children.Clear();

        foreach (var annotation in _annotations)
        {
            Draw(annotation);
        }

        if (preview is not null)
        {
            Draw(preview);
        }
    }

    private void Draw(PhotoAnnotation annotation)
    {
        var stroke = new SolidColorBrush(MarkColor.Color);
        var thickness = 3d;

        if (annotation.Kind == PhotoAnnotationKind.Circle)
        {
            var radius = annotation.Radius * _scale;
            if (radius <= 0)
            {
                return;
            }

            var circle = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = stroke,
                StrokeThickness = thickness,
                Fill = Brushes.Transparent,
            };

            Canvas.SetLeft(circle, annotation.X1 * _scale - radius);
            Canvas.SetTop(circle, annotation.Y1 * _scale - radius);
            Overlay.Children.Add(circle);
            return;
        }

        var line = new Line
        {
            X1 = annotation.X1 * _scale,
            Y1 = annotation.Y1 * _scale,
            X2 = annotation.X2 * _scale,
            Y2 = annotation.Y2 * _scale,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Overlay.Children.Add(line);

        var head = ProjectAnalysisPhotoCatalog.ArrowHead(annotation, 18 / Math.Max(_scale, 0.0001));
        var arrow = new Polygon
        {
            Fill = stroke,
            Points = new PointCollection(
                head.Select(point => new Point(point.X * _scale, point.Y * _scale))),
        };
        Overlay.Children.Add(arrow);
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_annotations.Count > 0)
        {
            _annotations.RemoveAt(_annotations.Count - 1);
            Redraw();
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _annotations.Clear();
        Redraw();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static PhotoAnnotation Clone(PhotoAnnotation annotation) => new()
    {
        Kind = annotation.Kind,
        X1 = annotation.X1,
        Y1 = annotation.Y1,
        X2 = annotation.X2,
        Y2 = annotation.Y2,
        Radius = annotation.Radius,
    };

    /// <summary>Größe des Bildes in Pixeln; 0, wenn das Format nicht lesbar ist.</summary>
    private static (double Width, double Height) ReadPixelSize(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
        {
            return (0, 0);
        }
    }

    private static ImageSource? LoadImage(string filePath)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(filePath);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
