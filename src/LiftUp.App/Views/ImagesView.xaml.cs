using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiftUp.Core.Domain;
using Microsoft.Win32;

namespace LiftUp.App.Views;

public partial class ImagesView : UserControl
{
    private sealed record ProjectOption(Guid? Id, string Name);

    private const string AllCategories = "Alle Kategorien";
    /// <summary>Gewählte Schlagworte; leer bedeutet „nicht filtern“.</summary>
    private readonly HashSet<string> _selectedTags = new(StringComparer.CurrentCultureIgnoreCase);

    private IReadOnlyList<string> _availableTags = [];

    /// <summary>Für die Hervorhebung in der Liste: die gerade aktiven Filter-Schlagworte.</summary>
    public IReadOnlyCollection<string> ActiveTags => _selectedTags;

    private readonly AppServices _services;
    private readonly DispatcherTimer _folderTimer;
    private IReadOnlyList<ImageListItem> _items = [];
    private FileSystemWatcher? _folderWatcher;
    private bool _syncingFromWatcher;

    public ImagesView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        // Änderungen im Projektordner werden gebündelt und dann eingelesen.
        _folderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _folderTimer.Tick += OnFolderTimerTick;
        Loaded += (_, _) => StartFolderWatcher();
        Unloaded += (_, _) => StopFolderWatcher();

        LoadProjects();
        LoadImages();
    }

    private void LoadProjects()
    {
        var options = new List<ProjectOption> { new(null, "Globale Bibliothek") };

        try
        {
            options.AddRange(_services.Projects.Search(new ProjectQuery())
                .Select(project => new ProjectOption(project.Id, $"{project.Number} · {project.Name}")));
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }

        ProjectBox.ItemsSource = options;
        ProjectBox.SelectedIndex = 0;
    }

    private Guid? SelectedProjectId => (ProjectBox.SelectedItem as ProjectOption)?.Id;

    private void LoadImages()
    {
        try
        {
            _items = _services.Images.Search(SelectedProjectId, SearchBox.Text, ArchiveToggle.IsChecked == true);

            var previousCategory = CategoryFilterBox.SelectedItem as string;

            var categories = new List<string> { AllCategories };
            categories.AddRange(_items
                .Select(item => item.Category)
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase));

            var tags = _items
                .SelectMany(item => ImageTags.Split(item.Tags))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            _availableTags = tags;
            _selectedTags.RemoveWhere(tag => !tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase));

            CategoryFilterBox.ItemsSource = categories;
            CategoryFilterBox.SelectedItem = previousCategory is not null && categories.Contains(previousCategory)
                ? previousCategory
                : AllCategories;

            UpdateTagFilterButton();

            ApplyCategoryFilter();
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    /// <summary>Grenzt die Liste nach Kategorie und Schlagwort ein; beide Filter gelten zusammen.</summary>
    private void ApplyCategoryFilter()
    {
        var category = CategoryFilterBox.SelectedItem as string;
        IEnumerable<ImageListItem> filtered = _items;

        if (!string.IsNullOrWhiteSpace(category) && category != AllCategories)
        {
            filtered = filtered.Where(item =>
                string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedTags.Count > 0)
        {
            filtered = filtered.Where(item => ImageTags.MatchesAny(item.Tags, _selectedTags));
        }

        var result = filtered.ToArray();
        ImagesGrid.ItemsSource = result;
        UpdateBulkTagsButton();

        var missing = result.Count(item => !item.FileExists);
        RemoveMissingButton.IsEnabled = missing > 0;
        RemoveMissingButton.Content = missing switch
        {
            0 => "Fehlende entfernen",
            1 => "1 fehlende Datei aufräumen",
            _ => $"{missing} fehlende Dateien aufräumen",
        };

        InfoText.Text = result.Length == 1 ? "1 Bild" : $"{result.Length} Bilder";
    }

    /// <summary>
    /// Filtert direkt über ein Schlagwort in der Liste. Ein erneuter Klick auf dasselbe
    /// Schlagwort hebt den Filter wieder auf.
    /// </summary>
    private void OnTagClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        if (!_selectedTags.Remove(tag))
        {
            _selectedTags.Add(tag);
        }

        UpdateTagFilterButton();
        ApplyCategoryFilter();
        e.Handled = true;
    }

    /// <summary>Zeigt die aktuelle Schlagwortauswahl auf dem Knopf.</summary>
    private void UpdateTagFilterButton()
    {
        TagFilterButton.Content = _selectedTags.Count switch
        {
            0 => "Alle",
            1 => _selectedTags.First(),
            _ => $"{_selectedTags.Count} ausgewählt",
        };

        TagFilterButton.ToolTip = _selectedTags.Count == 0
            ? "Mehrere Schlagworte gleichzeitig wählen"
            : string.Join(", ", _selectedTags.OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// Öffnet die Schlagwortliste als Aufklappmenü mit Häkchen. Mehrere Schlagworte
    /// lassen sich gleichzeitig wählen; ein Bild genügt, wenn eines davon passt.
    /// </summary>
    private void OnTagFilterClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TagFilterButton, Placement = PlacementMode.Bottom };
        var tagItems = new List<MenuItem>();

        var all = new MenuItem
        {
            Header = "Alle Schlagworte",
            IsCheckable = true,
            IsChecked = _selectedTags.Count == 0,
            StaysOpenOnClick = true,
        };

        all.Click += (_, _) =>
        {
            _selectedTags.Clear();
            foreach (var item in tagItems)
            {
                item.IsChecked = false;
            }

            UpdateTagFilterButton();
            ApplyCategoryFilter();
        };

        menu.Items.Add(all);
        menu.Items.Add(new Separator());

        foreach (var tag in _availableTags)
        {
            var item = new MenuItem
            {
                Header = tag,
                IsCheckable = true,
                IsChecked = _selectedTags.Contains(tag),
                StaysOpenOnClick = true,
            };

            item.Click += (_, _) =>
            {
                if (!_selectedTags.Remove(tag))
                {
                    _selectedTags.Add(tag);
                }

                item.IsChecked = _selectedTags.Contains(tag);
                all.IsChecked = _selectedTags.Count == 0;
                UpdateTagFilterButton();
                ApplyCategoryFilter();
            };

            tagItems.Add(item);
            menu.Items.Add(item);
        }

        if (_availableTags.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Keine Schlagworte vorhanden", IsEnabled = false });
        }

        menu.IsOpen = true;
    }

    private ImageListItem? Selected => ImagesGrid.SelectedItem as ImageListItem;

    /// <summary>Mehrfachauswahl für Sammeländerungen an Schlagworten.</summary>
    private IReadOnlyList<ImageListItem> SelectedItems =>
        ImagesGrid.SelectedItems.Cast<ImageListItem>().ToArray();

    private void UpdateBulkTagsButton()
    {
        var count = ImagesGrid.SelectedItems.Count;
        BulkTagsButton.IsEnabled = count > 0;
        BulkTagsButton.Content = count > 1 ? $"Schlagworte ({count}) …" : "Schlagworte …";
    }

    /// <summary>
    /// Setzt oder entfernt Schlagworte für alle markierten Bilder in einem Schritt.
    /// Mehrere Bilder lassen sich mit Strg- oder Umschalt-Klick markieren.
    /// </summary>
    private void OnBulkTagsClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedItems;
        if (selected.Count == 0)
        {
            InfoText.Text = "Bitte zuerst Bilder markieren (Strg oder Umschalt gedrückt halten).";
            return;
        }

        var window = new ImageTagsWindow(selected, _availableTags)
        {
            Owner = Window.GetWindow(this),
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var changed = _services.Images.SetTags(
                selected.Select(item => item.Id).ToArray(), window.AddTags, window.RemoveTags);

            LoadImages();
            InfoText.Text = changed switch
            {
                0 => "Die Schlagworte waren bereits so gesetzt.",
                1 => "Schlagworte von 1 Bild aktualisiert.",
                _ => $"Schlagworte von {changed} Bildern aktualisiert.",
            };
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    /// <summary>Gleicht den Projektordner mit der Bildbibliothek ab; 0 ohne neues Bild.</summary>
    private ProjectFolderSyncResult SyncProjectFolder()
    {
        if (SelectedProjectId is not { } projectId)
        {
            return new ProjectFolderSyncResult(0, 0);
        }

        try
        {
            return _services.Images.SyncProjectFolder(projectId);
        }
        catch (PermissionDeniedException)
        {
            return new ProjectFolderSyncResult(0, 0);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
            return new ProjectFolderSyncResult(0, 0);
        }
    }

    /// <summary>Nennt in Worten, was der Ordnerabgleich gefunden hat.</summary>
    private static string DescribeSync(ProjectFolderSyncResult sync) => (sync.Added, sync.Moved) switch
    {
        (0, 0) => "Keine neuen Bilder im Projektordner gefunden.",
        (0, 1) => "1 verschobenes oder umbenanntes Bild wiedererkannt und den Pfad aktualisiert.",
        (0, _) => $"{sync.Moved} verschobene oder umbenannte Bilder wiedererkannt und die Pfade aktualisiert.",
        (1, 0) => "1 Bild aus dem Projektordner übernommen.",
        (_, 0) => $"{sync.Added} Bilder aus dem Projektordner übernommen.",
        (1, _) => $"1 Bild übernommen, {sync.Moved} verschobene oder umbenannte wiedererkannt.",
        _ => $"{sync.Added} Bilder übernommen, {sync.Moved} verschobene oder umbenannte wiedererkannt.",
    };

    /// <summary>Entfernt Einträge, deren Bilddatei nicht mehr existiert.</summary>
    private void OnRemoveMissingClick(object sender, RoutedEventArgs e)
    {
        var missing = _items.Count(item => !item.FileExists);
        if (missing == 0)
        {
            InfoText.Text = "Alle Bilddateien sind vorhanden.";
            return;
        }

        var answer = MessageBox.Show(
            missing == 1
                ? "Ein Eintrag verweist auf eine Datei, die nicht mehr existiert. Eintrag entfernen?"
                : $"{missing} Einträge verweisen auf Dateien, die nicht mehr existieren. Einträge entfernen?",
            "Fehlende Bilder entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var removed = _services.Images.RemoveMissing(SelectedProjectId);
            LoadImages();
            InfoText.Text = removed switch
            {
                0 => "Es waren keine fehlenden Dateien mehr vorhanden.",
                1 => "1 fehlender Eintrag entfernt.",
                _ => $"{removed} fehlende Einträge entfernt.",
            };
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnScanFolderClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProjectId is null)
        {
            InfoText.Text = "Bitte zuerst ein Projekt wählen – die globale Bibliothek hat keinen Projektordner.";
            return;
        }

        var sync = SyncProjectFolder();
        LoadImages();
        InfoText.Text = DescribeSync(sync);
    }

    /// <summary>
    /// Beim Wechsel des Bereichs wird der Projektordner eingelesen: Bilder, die dort
    /// gespeichert wurden, erscheinen ohne Import in der Liste.
    /// </summary>
    private void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        var sync = SyncProjectFolder();
        LoadImages();

        if (sync.Any)
        {
            InfoText.Text = DescribeSync(sync);
        }

        StartFolderWatcher();
    }

    /// <summary>
    /// Beobachtet den Projektordner, damit neue, verschobene und umbenannte Bilder ohne
    /// manuellen Abgleich erscheinen. Die Ereignisse werden gebündelt, damit eine größere
    /// Änderung nicht viele Abgleiche auf einmal auslöst.
    /// </summary>
    private void StartFolderWatcher()
    {
        StopFolderWatcher();

        if (SelectedProjectId is not { } projectId)
        {
            return;
        }

        try
        {
            var project = _services.Projects.GetProject(projectId);
            if (string.IsNullOrWhiteSpace(project.FolderPath) || !Directory.Exists(project.FolderPath))
            {
                return;
            }

            _folderWatcher = new FileSystemWatcher(project.FolderPath)
            {
                IncludeSubdirectories = true,
                Filter = "*.*",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            _folderWatcher.Created += OnFolderChanged;
            _folderWatcher.Changed += OnFolderChanged;
            _folderWatcher.Deleted += OnFolderChanged;
            _folderWatcher.Renamed += OnFolderRenamed;
            _folderWatcher.Error += OnFolderWatcherError;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LiftUpException)
        {
            InfoText.Text = "Der Projektordner kann nicht überwacht werden: " + exception.Message;
        }
    }

    private void StopFolderWatcher()
    {
        _folderTimer.Stop();

        if (_folderWatcher is null)
        {
            return;
        }

        _folderWatcher.EnableRaisingEvents = false;
        _folderWatcher.Created -= OnFolderChanged;
        _folderWatcher.Changed -= OnFolderChanged;
        _folderWatcher.Deleted -= OnFolderChanged;
        _folderWatcher.Renamed -= OnFolderRenamed;
        _folderWatcher.Error -= OnFolderWatcherError;
        _folderWatcher.Dispose();
        _folderWatcher = null;
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e) => QueueFolderSync();

    private void OnFolderRenamed(object sender, RenamedEventArgs e) => QueueFolderSync();

    private void OnFolderWatcherError(object sender, ErrorEventArgs e) => QueueFolderSync();

    /// <summary>Dateiereignisse kommen aus dem Hintergrund; der Zeitgeber gehört zur Oberfläche.</summary>
    private void QueueFolderSync() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_folderWatcher is null)
        {
            return;
        }

        _folderTimer.Stop();
        _folderTimer.Start();
    }));

    private void OnFolderTimerTick(object? sender, EventArgs e)
    {
        _folderTimer.Stop();
        if (_syncingFromWatcher || _folderWatcher is null)
        {
            return;
        }

        _syncingFromWatcher = true;
        try
        {
            var sync = SyncProjectFolder();
            if (sync.Any)
            {
                LoadImages();
                InfoText.Text = DescribeSync(sync);
            }
        }
        finally
        {
            _syncingFromWatcher = false;
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => LoadImages();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => LoadImages();

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e) => ApplyCategoryFilter();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => LoadImages();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = Selected;
        EditButton.IsEnabled = selected is not null;
        ArchiveButton.IsEnabled = selected is not null;
        DeleteButton.IsEnabled = selected is not null;
        ArchiveButton.Content = selected?.IsArchived == true ? "Wiederherstellen" : "Archivieren";
        UpdateBulkTagsButton();

        PreviewImage.Source = null;
        PlaceholderText.Text = string.Empty;

        if (selected is null)
        {
            return;
        }

        if (!File.Exists(selected.FilePath))
        {
            PlaceholderText.Text = $"Die Bilddatei fehlt:\n{selected.FilePath}\n\nÜber „Fehlende entfernen“ lässt sich der Eintrag aufräumen.";
            return;
        }

        PlaceholderText.Text = string.IsNullOrWhiteSpace(selected.Tags)
            ? selected.Placeholder
            : $"{selected.Placeholder}\nSchlagworte: {selected.Tags}";

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(selected.FilePath);
            bitmap.EndInit();
            PreviewImage.Source = bitmap;
        }
        catch (Exception exception)
        {
            PlaceholderText.Text = "Vorschau nicht möglich: " + exception.Message;
        }
    }

    /// <summary>
    /// Importiert ein oder mehrere Bilder. Name, Kategorie und Schlagworte werden in
    /// einem Fenster je Bild festgelegt; Fehler einzelner Dateien brechen den Rest nicht ab.
    /// </summary>
    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Bilder auswählen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.svg;*.tif;*.tiff|Alle Dateien|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var properties = new ImagePropertiesWindow(dialog.FileNames, SelectedProjectId)
        {
            Owner = Window.GetWindow(this),
        };

        if (properties.ShowDialog() != true)
        {
            return;
        }

        var imported = 0;
        var errors = new List<string>();

        foreach (var row in properties.Rows)
        {
            try
            {
                _services.Images.Import(row.FilePath, new ImageInput
                {
                    Name = row.Name,
                    Category = row.Category,
                    Tags = row.Tags,
                    ProjectId = SelectedProjectId,
                });
                imported++;
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(row.FilePath)}: {exception.Message}");
            }
        }

        LoadImages();
        InfoText.Text = imported == 1 ? "1 Bild importiert." : $"{imported} Bilder importiert.";

        if (errors.Count > 0)
        {
            MessageBox.Show(
                string.Join("\n", errors),
                "Import nicht vollständig",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>Ändert Name, Kategorie und Schlagworte eines bereits importierten Bildes.</summary>
    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            return;
        }

        try
        {
            var properties = new ImagePropertiesWindow(_services.Images.Get(selected.Id))
            {
                Owner = Window.GetWindow(this),
            };

            if (properties.ShowDialog() != true || properties.Single is not { } row)
            {
                return;
            }

            _services.Images.Update(selected.Id, new ImageInput
            {
                Name = row.Name,
                Category = row.Category,
                Tags = row.Tags,
            });

            LoadImages();
            InfoText.Text = $"Gespeichert: {row.Name}";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Bearbeiten fehlgeschlagen: " + exception.Message;
        }
    }

    private void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            return;
        }

        try
        {
            _services.Images.SetArchived(selected.Id, !selected.IsArchived);
            LoadImages();
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Bild \"{selected.Name}\" wirklich löschen?\n\n" +
            "Der Eintrag wird entfernt und die Bilddatei in den Papierkorb gelegt. " +
            "Bereits erzeugte PDFs behalten das eingebettete Bild.",
            "Bild löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var warning = _services.Images.Delete(selected.Id);
            LoadImages();
            InfoText.Text = warning ?? $"Gelöscht: {selected.Name}";

            if (warning is not null)
            {
                MessageBox.Show(warning, "Bild löschen", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }
}
