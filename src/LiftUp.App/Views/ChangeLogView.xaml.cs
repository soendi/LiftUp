using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;

namespace LiftUp.App.Views;

/// <summary>
/// Zeigt das Änderungsprotokoll: wer wann was in welchem Bereich geändert hat.
/// Gelesen wird nur – geschrieben wird es von den Diensten selbst.
/// </summary>
public partial class ChangeLogView : UserControl
{
    private const string AllEntries = "Alle";

    private readonly AppServices _services;
    private bool _loading;

    public ChangeLogView(AppServices services)
    {
        _services = services;
        InitializeComponent();
        LoadChoices();
        LoadLog();
    }

    private void LoadChoices()
    {
        _loading = true;
        try
        {
            EntityBox.Items.Clear();
            AddChoice(EntityBox, AllEntries, null);
            foreach (var type in _services.ChangeLog.GetEntityTypes())
            {
                AddChoice(EntityBox, ChangeLogService.DescribeEntity(type), type);
            }

            EntityBox.SelectedIndex = 0;

            ActorBox.Items.Clear();
            AddChoice(ActorBox, AllEntries, null);
            foreach (var actor in _services.ChangeLog.GetActors())
            {
                AddChoice(ActorBox, actor, actor);
            }

            ActorBox.SelectedIndex = 0;
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private static void AddChoice(ComboBox box, string text, string? value) =>
        box.Items.Add(new ComboBoxItem { Content = text, Tag = value });

    private static string? SelectedValue(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void LoadLog()
    {
        try
        {
            var filter = new ChangeLogFilter(
                EntityType: SelectedValue(EntityBox),
                Actor: SelectedValue(ActorBox),
                Search: string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text,
                From: FromPicker.SelectedDate,
                To: ToPicker.SelectedDate);

            var entries = _services.ChangeLog.Query(filter);
            LogGrid.ItemsSource = entries;

            InfoText.Text = entries.Count == 0
                ? "Keine Einträge für diese Auswahl."
                : $"{entries.Count} Einträge, neueste zuerst.";

            if (entries.Count == 0)
            {
                PayloadBox.Text = string.Empty;
            }
            else
            {
                LogGrid.SelectedIndex = 0;
            }
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LogGrid.SelectedItem is ChangeLogListItem item)
        {
            PayloadBox.Text = item.PayloadText;
        }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            LoadLog();
        }
    }

    private void OnDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            LoadLog();
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            LoadLog();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        LoadChoices();
        LoadLog();
    }
}
