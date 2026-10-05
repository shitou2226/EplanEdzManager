using System.Globalization;
using System.Windows.Data;

namespace EplanEdzManager.Desktop;

public sealed class BooleanToFavoriteGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "★" : "☆";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
