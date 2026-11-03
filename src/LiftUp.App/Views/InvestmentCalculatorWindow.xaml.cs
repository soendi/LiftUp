using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

/// <summary>
/// Rechner zur Investitionsplanung: links und rechts je eine Liste von Massnahmen mit Preis.
/// Die Summe je Seite landet in der Spalte „Kosten“ der Tabelle, die Massnahmen in der
/// Spalte „Massnahme“ – Tabelle und Fenster zeigen damit immer dasselbe.
/// </summary>
public partial class InvestmentCalculatorWindow : Window
{
    /// <summary>Eine Massnahme mit ihrem Preis, wie sie im Rechner erfasst wird.</summary>
    public sealed class MeasureRow : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _price = string.Empty;

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value)
                {
                    return;
                }

                _name = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public string Price
        {
            get => _price;
            set
            {
                if (_price == value)
                {
                    return;
                }

                _price = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Price)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Ein Eintrag der gespeicherten Preise je Massnahme.</summary>
    private sealed class BreakdownEntry
    {
        public string? Name { get; set; }
        public string? Price { get; set; }
    }

    /// <summary>
    /// Die Preise stehen als <c>{"name":…,"price":…}</c> in der Zeile. Ohne
    /// <see cref="JsonSerializerDefaults.Web"/> liest der Serialisierer die Schlüssel
    /// gross/klein-empfindlich – die Preise kämen dann leer zurück, obwohl sie gespeichert sind.
    /// </summary>
    private static readonly JsonSerializerOptions BreakdownJson = new(JsonSerializerDefaults.Web);

    private readonly ObservableCollection<MeasureRow> _left = [];
    private readonly ObservableCollection<MeasureRow> _right = [];

    public InvestmentCalculatorWindow(
        string leftTitle,
        string leftMeasures,
        string leftBreakdown,
        string rightTitle,
        string rightMeasures,
        string rightBreakdown)
    {
        InitializeComponent();

        LeftTitle.Text = leftTitle;
        RightTitle.Text = rightTitle;

        FillRows(_left, leftMeasures, leftBreakdown);
        FillRows(_right, rightMeasures, rightBreakdown);

        LeftGrid.ItemsSource = _left;
        RightGrid.ItemsSource = _right;

        Track(_left);
        Track(_right);

        // Eine geänderte Zelle wirkt sich erst nach dem Verlassen aus – die Summe folgt deshalb
        // im nächsten Durchlauf des Meldungs-Laufs.
        LeftGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateSums));
        RightGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateSums));

        UpdateSums();
    }

    /// <summary>Massnahmen der linken Seite, eine je Zeile.</summary>
    public string LeftMeasures { get; private set; } = string.Empty;

    /// <summary>Summe der linken Seite; leer, solange kein Preis erfasst ist.</summary>
    public string LeftCost { get; private set; } = string.Empty;

    /// <summary>Preise der linken Seite als JSON – Grundlage beim nächsten Öffnen.</summary>
    public string LeftBreakdown { get; private set; } = string.Empty;

    public string RightMeasures { get; private set; } = string.Empty;

    public string RightCost { get; private set; } = string.Empty;

    public string RightBreakdown { get; private set; } = string.Empty;

    /// <summary>Übernimmt die Preise in die Summenanzeige, sobald eine Zeile dazukommt oder sich ändert.</summary>
    private void Track(ObservableCollection<MeasureRow> rows)
    {
        // Auch die aus der Tabelle übernommenen Zeilen melden jede Änderung – sonst bliebe
        // die Summe stehen, bis die Zelle verlassen wird.
        foreach (var row in rows)
        {
            row.PropertyChanged += OnMeasureChanged;
        }

        rows.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
            {
                foreach (MeasureRow row in args.NewItems)
                {
                    row.PropertyChanged += OnMeasureChanged;
                }
            }

            UpdateSums();
        };
    }

    private void OnMeasureChanged(object? sender, PropertyChangedEventArgs e) => UpdateSums();

    /// <summary>
    /// Füllt eine Seite aus der Tabelle: Die Massnahmen stammen aus der Spalte „Massnahme“,
    /// die Preise aus dem gespeicherten JSON. Ohne Massnahmen in der Tabelle stehen die
    /// gespeicherten Einträge im Fenster.
    /// </summary>
    private static void FillRows(ICollection<MeasureRow> target, string measures, string breakdown)
    {
        var prices = ParseBreakdown(breakdown);

        var names = (measures ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (names.Count == 0)
        {
            foreach (var (name, price) in prices)
            {
                target.Add(new MeasureRow { Name = name, Price = price });
            }

            return;
        }

        foreach (var name in names)
        {
            var price = prices
                .FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                .Price;

            target.Add(new MeasureRow { Name = name, Price = price ?? string.Empty });
        }
    }

    private static List<(string Name, string Price)> ParseBreakdown(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<BreakdownEntry>>(json, BreakdownJson);
            return entries is null
                ? []
                : entries
                    .Where(entry => entry is not null)
                    .Select(entry => (entry.Name ?? string.Empty, entry.Price ?? string.Empty))
                    .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Serialize(IReadOnlyList<MeasureRow> rows) =>
        rows.Count == 0
            ? string.Empty
            : JsonSerializer.Serialize(rows.Select(row => new { name = row.Name, price = row.Price }), BreakdownJson);

    private void UpdateSums()
    {
        LeftSum.Text = FormatSum(_left);
        RightSum.Text = FormatSum(_right);
    }

    private static string FormatSum(IEnumerable<MeasureRow> rows) =>
        PriceText.Format(Sum(rows));

    private static decimal Sum(IEnumerable<MeasureRow> rows) => rows.Sum(row => PriceText.Parse(row.Price));

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        // Eine offene Zelle gehört noch zum Ergebnis.
        CommitEdit(LeftGrid);
        CommitEdit(RightGrid);

        var left = Clean(_left);
        var right = Clean(_right);

        LeftMeasures = Join(left);
        LeftCost = FormatCost(left);
        LeftBreakdown = Serialize(left);

        RightMeasures = Join(right);
        RightCost = FormatCost(right);
        RightBreakdown = Serialize(right);

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void CommitEdit(DataGrid grid)
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    /// <summary>Lässt leere Zeilen weg und schneidet die Eingaben zurecht.</summary>
    private static List<MeasureRow> Clean(IEnumerable<MeasureRow> rows) => rows
        .Select(row => new MeasureRow { Name = row.Name.Trim(), Price = row.Price.Trim() })
        .Where(row => row.Name.Length > 0 || row.Price.Length > 0)
        .ToList();

    /// <summary>Die Massnahmen als Text der Tabellenzelle – eine je Zeile.</summary>
    private static string Join(IEnumerable<MeasureRow> rows) => string.Join(
        "\n",
        rows.Where(row => row.Name.Length > 0).Select(row => row.Name));

    private static string FormatCost(IReadOnlyList<MeasureRow> rows)
    {
        var sum = Sum(rows);
        return sum == 0m ? string.Empty : PriceText.Format(sum);
    }
}
