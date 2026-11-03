using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LiftUp.Core.Domain;
using Microsoft.Win32;

namespace LiftUp.App.Views;

/// <summary>
/// Fotos eines Aufzugs: Liste links, Vorschau rechts. Neue Fotos landen im Bilderordner
/// des Projekts; Markierungen (Kreis, Pfeil) entstehen im kleinen Bild-Editor.
/// </summary>
public sealed class LiftPhotosPanel : Grid
{
    private readonly AppServices _services;
    private readonly Guid _projectId;
    private readonly string _liftKey;
    private readonly ObservableCollection<PhotoRow> _rows = [];

    private readonly ListBox _list = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
    private readonly TextBox _caption = new();
    private readonly TextBlock _info = new();
    private readonly Button _edit = new() { Content = "Markieren …", IsEnabled = false };
    private readonly Button _remove = new() { Content = "Entfernen", IsEnabled = false };

    /// <summary>Hinweis auf der leeren Vorschaufläche: Bilder lassen sich hierher ziehen.</summary>
    private readonly TextBlock _dropHint = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(20),
    };

    /// <summary>Meldet, wenn Fotos hinzukommen oder wegfallen – für die Farbe des Reiters.</summary>
    public event EventHandler? PhotosChanged;

    /// <summary>Anzahl der Fotos in der Liste.</summary>
    public int PhotoCount => _rows.Count;

    public LiftPhotosPanel(AppServices services, Guid projectId, string liftKey)
    {
        _services = services;
        _projectId = projectId;
        _liftKey = liftKey;

        // Bilder aus dem Explorer lassen sich auf die ganze Foto-Fläche ziehen.
        AllowDrop = true;
        DragOver += OnFilesDragOver;
        Drop += OnFilesDrop;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Children.Add(BuildToolbar());

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _list.DisplayMemberPath = nameof(PhotoRow.DisplayText);
        _list.Margin = new Thickness(0, 0, 10, 0);
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        _list.ItemsSource = _rows;

        _dropHint.Text = "Bilder hierher ziehen – sie werden in den Bilderordner des Projekts kopiert.";
        _dropHint.Style = (Style)Application.Current.FindResource("LiftUpMutedText");

        var previewArea = new Grid();
        previewArea.Children.Add(_preview);
        previewArea.Children.Add(_dropHint);

        var previewBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xE2, 0xEC)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = previewArea,
        };

        body.Children.Add(_list);
        Grid.SetColumn(previewBorder, 1);
        body.Children.Add(previewBorder);

        Grid.SetRow(body, 1);
        Children.Add(body);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        Children.Add(footer);
    }

    private FrameworkElement BuildToolbar()
    {
        var add = new Button { Content = "Fotos hinzufügen …" };
        add.Click += OnAddClick;

        _edit.Click += OnEditClick;
        _remove.Click += OnRemoveClick;

        var save = new Button
        {
            Content = "Speichern",
            Style = (Style)Application.Current.FindResource("LiftUpPrimaryButton"),
            Margin = new Thickness(0),
        };
        save.Click += OnSaveClick;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(add);
        row.Children.Add(_edit);
        row.Children.Add(_remove);
        row.Children.Add(save);
        return row;
    }

    /// <summary>Bildunterschrift des gewählten Fotos und der Stand der Liste.</summary>
    private FrameworkElement BuildFooter()
    {
        _caption.SetBinding(TextBox.TextProperty, new Binding("SelectedItem.Caption")
        {
            Source = _list,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        _caption.Margin = new Thickness(0, 0, 0, 6);
        _caption.IsEnabled = false;
        _list.SelectionChanged += (_, _) => _caption.IsEnabled = _list.SelectedItem is not null;

        _info.Style = (Style)Application.Current.FindResource("LiftUpMutedText");

        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "Bildunterschrift",
            Style = (Style)Application.Current.FindResource("LiftUpFieldLabel"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(_caption);
        panel.Children.Add(_info);
        return panel;
    }

    private void OnSelectionChanged()
    {
        var row = _list.SelectedItem as PhotoRow;
        _edit.IsEnabled = row is not null;
        _remove.IsEnabled = row is not null;
        _preview.Source = row?.Preview;
        _dropHint.Visibility = _preview.Source is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Lädt die gespeicherten Fotos dieses Aufzugs.</summary>
    public void Load()
    {
        _rows.Clear();

        try
        {
            foreach (var photo in _services.Analysis.GetPhotos(_projectId)
                         .Where(photo => string.Equals(photo.LiftKey, _liftKey, StringComparison.OrdinalIgnoreCase)))
            {
                _rows.Add(new PhotoRow(photo));
            }

            _info.Text = _rows.Count == 1
                ? "1 Foto – erscheint als eigene Seite im PDF."
                : $"{_rows.Count} Fotos – erscheinen als eigene Seiten im PDF.";
        }
        catch (LiftUpException exception)
        {
            _info.Text = exception.Message;
        }
    }

    /// <summary>
    /// Bilderordner des Projekts. Die Auswahl startet dort, damit die Fotos aus dem Projekt
    /// nicht erst gesucht werden müssen; fehlt der Ordner, bleibt der zuletzt geöffnete.
    /// </summary>
    private string? PhotoFolder()
    {
        try
        {
            var folder = Path.Combine(_services.Projects.GetProject(_projectId).FolderPath, ProjectFolders.Images);
            return Directory.Exists(folder) ? folder : null;
        }
        catch (LiftUpException)
        {
            return null;
        }
    }

    /// <summary>Übernimmt ein oder mehrere Fotos; ein Fehler bricht die übrigen nicht ab.</summary>
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Fotos hinzufügen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.svg|Alle Dateien|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (PhotoFolder() is { } folder)
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        AddPhotos(dialog.FileNames);
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Bilder, die aus dem Explorer auf die Fläche gezogen wurden.</summary>
    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        e.Handled = true;
        AddPhotos(files);
    }

    /// <summary>
    /// Übernimmt Bilddateien in den Bilderordner des Projekts und stellt sie in die Liste.
    /// Ein Fehler bricht die übrigen nicht ab.
    /// </summary>
    private void AddPhotos(IReadOnlyList<string> files)
    {
        PhotoRow? first = null;
        var added = 0;
        var errors = new List<string>();

        foreach (var file in files)
        {
            try
            {
                var path = _services.Analysis.ImportPhoto(_projectId, file);

                // Die Aufzugsnummer steht als Schlagwort am Bild; so lässt es sich in der
                // Bildbibliothek nach Aufzug filtern.
                _services.Images.TryAddTagToFile(_projectId, path, _liftKey);
                var (width, height) = ReadPixelSize(path);
                var row = new PhotoRow(new ProjectAnalysisPhoto
                {
                    ProjectId = _projectId,
                    LiftKey = _liftKey,
                    FilePath = path,
                    ImageWidth = width,
                    ImageHeight = height,
                });

                _rows.Add(row);
                first ??= row;
                added++;
            }
            catch (LiftUpException exception)
            {
                errors.Add($"{Path.GetFileName(file)}: {exception.Message}");
            }
        }

        if (first is not null)
        {
            _list.SelectedItem = first;
        }

        if (added == 0)
        {
            _info.Text = errors.Count > 0 ? string.Join(" ", errors) : "Keine Bilder übernommen.";
            return;
        }

        _info.Text = added == 1
            ? "Foto übernommen – bitte speichern."
            : $"{added} Fotos übernommen – bitte speichern.";

        if (errors.Count > 0)
        {
            _info.Text += " " + string.Join(" ", errors);
        }

        PhotosChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not PhotoRow row)
        {
            return;
        }

        if (!File.Exists(row.Photo.FilePath))
        {
            _info.Text = "Die Bilddatei ist nicht mehr vorhanden.";
            return;
        }

        var dialog = new PhotoAnnotationWindow(row.Photo.FilePath, row.Photo.Annotations)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        row.Photo.Annotations = [.. dialog.Annotations];
        row.Notify(nameof(PhotoRow.DisplayText));

        // Die Anzeige lädt das Bild neu; sonst bliebe das Foto ohne Markierung stehen.
        OnSelectionChanged();

        _info.Text = dialog.Annotations.Count == 0
            ? "Markierungen entfernt – bitte speichern."
            : $"{dialog.Annotations.Count} Markierung(en) übernommen – bitte speichern.";
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not PhotoRow row)
        {
            return;
        }

        _rows.Remove(row);
        _info.Text = "Foto entfernt – bitte speichern. Die Bilddatei bleibt im Projektordner.";
        PhotosChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = _services.Analysis.SavePhotos(
                _projectId,
                _liftKey,
                _rows.Select(row => new ProjectAnalysisPhotoInput(
                        row.Photo.FilePath,
                        row.Photo.Caption,
                        row.Photo.Annotations,
                        row.Photo.ImageWidth,
                        row.Photo.ImageHeight))
                    .ToArray());

            _rows.Clear();
            foreach (var photo in saved)
            {
                _rows.Add(new PhotoRow(photo));
            }

            _info.Text = saved.Count == 1 ? "Gespeichert: 1 Foto." : $"Gespeichert: {saved.Count} Fotos.";
            PhotosChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (LiftUpException exception)
        {
            _info.Text = exception.Message;
        }
    }

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
}

/// <summary>Ein Foto in der Liste; die Unterschrift wird direkt am Foto gepflegt.</summary>
public sealed class PhotoRow : INotifyPropertyChanged
{
    /// <summary>Farbe der Markierungen – dieselbe wie im Bild-Editor.</summary>
    private static readonly Color MarkColor = Color.FromRgb(0xE1, 0x1D, 0x48);

    public PhotoRow(ProjectAnalysisPhoto photo)
    {
        Photo = photo;
    }

    public ProjectAnalysisPhoto Photo { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Caption
    {
        get => Photo.Caption;
        set
        {
            if (Photo.Caption == value)
            {
                return;
            }

            Photo.Caption = value ?? string.Empty;
            Notify(nameof(Caption));
            Notify(nameof(DisplayText));
        }
    }

    public string DisplayText => Photo.DisplayText;

    /// <summary>Vorschaubild für Liste und Anzeige; null, wenn die Datei fehlt.</summary>
    public ImageSource? Preview
    {
        get
        {
            if (!File.Exists(Photo.FilePath))
            {
                return null;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(Photo.FilePath);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.EndInit();
                image.Freeze();

                // Markierungen gehören in die Vorschau: sonst zeigt das Hauptfenster nur das
                // unveränderte Bild, obwohl das PDF die Markierung enthält.
                return Photo.Annotations.Count == 0 ? image : WithAnnotations(image);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Zeichnet Kreise und Pfeile über das Bild. Die Werte sind Bildpixel, deshalb sitzt die
    /// Markierung in der Vorschau an derselben Stelle wie im PDF.
    /// </summary>
    private ImageSource WithAnnotations(BitmapImage image)
    {
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return image;
        }

        var thickness = ProjectAnalysisPhotoCatalog.StrokeWidth(width, height);
        var pen = new Pen(new SolidColorBrush(MarkColor), thickness);
        pen.Freeze();
        var fill = new SolidColorBrush(MarkColor);
        fill.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(image, new Rect(0, 0, width, height));

            foreach (var annotation in Photo.Annotations)
            {
                if (annotation.Kind == PhotoAnnotationKind.Circle)
                {
                    if (annotation.Radius > 0)
                    {
                        context.DrawEllipse(null, pen, new Point(annotation.X1, annotation.Y1),
                            annotation.Radius, annotation.Radius);
                    }

                    continue;
                }

                context.DrawLine(pen,
                    new Point(annotation.X1, annotation.Y1),
                    new Point(annotation.X2, annotation.Y2));

                var head = ProjectAnalysisPhotoCatalog.ArrowHead(annotation, thickness * 5);
                var arrow = new StreamGeometry();
                using (var geometry = arrow.Open())
                {
                    geometry.BeginFigure(new Point(head[0].X, head[0].Y), isFilled: true, isClosed: true);
                    geometry.LineTo(new Point(head[1].X, head[1].Y), isStroked: false, isSmoothJoin: false);
                    geometry.LineTo(new Point(head[2].X, head[2].Y), isStroked: false, isSmoothJoin: false);
                }

                arrow.Freeze();
                context.DrawGeometry(fill, null, arrow);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
