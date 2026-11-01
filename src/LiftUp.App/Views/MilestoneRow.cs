using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiftUp.App.Views;

/// <summary>
/// Bearbeitbare Zeile der Meilenstein-Tabelle; sie wird im Projektdialog und in der
/// Projektübersicht verwendet. Änderungen melden sich, damit die Übersicht den
/// erledigt-Haken sofort speichern kann.
/// </summary>
public sealed class MilestoneRow : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private bool _isDone;
    private string _doneDateText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public bool IsDone
    {
        get => _isDone;
        set => Set(ref _isDone, value);
    }

    /// <summary>Fester Meilenstein (Begehung): Bezeichnung und Zeile sind nicht änderbar.</summary>
    public bool IsFixed { get; set; }

    /// <summary>Datum als Text in der Form TT.MM.JJJJ.</summary>
    public string DoneDateText
    {
        get => _doneDateText;
        set => Set(ref _doneDateText, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
