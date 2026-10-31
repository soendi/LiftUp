using System.Globalization;
using System.Windows.Data;

namespace LiftUp.App.Converters;

/// <summary>
/// Prüft, ob ein Schlagwort gerade als Filter aktiv ist. Die Bilderliste hebt damit
/// die gewählten Schlagworte direkt in der Zeile hervor.
/// </summary>
public sealed class TagActiveConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string tag || values[1] is not IEnumerable<string> active)
        {
            return false;
        }

        return active.Contains(tag, StringComparer.CurrentCultureIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
