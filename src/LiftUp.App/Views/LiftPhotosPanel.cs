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

    public LiftPhotosPanel(AppServices services, Guid projectId, string liftKey)
    {
        _services = services;
        _projectId = projectId;
        _liftKey = liftKey;

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

        var previewBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xE2, 0xEC)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = _preview,
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
        var add = new Button { Content = "Foto hinzufügen …" };
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

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Foto hinzufügen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.svg|Alle Dateien|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var path = _services.Analysis.ImportPhoto(_projectId, dialog.FileName);
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
            _list.SelectedItem = row;
            _info.Text = "Foto übernommen – bitte speichern.";
        }
        catch (LiftUpException exception)
        {
            _info.Text = exception.Message;
        }
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
                return image;
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }
    }

    public void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
