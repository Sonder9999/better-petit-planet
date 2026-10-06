using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BetterPetitPlanet.View.Converters;

public sealed class ProgressToMarginConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 &&
            values[0] is double progress &&
            values[1] is double totalWidth &&
            totalWidth > 0 &&
            !double.IsNaN(totalWidth))
        {
            double markerWidth = 8.0;
            if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedW))
            {
                markerWidth = parsedW;
            }

            double left = Math.Clamp(progress * totalWidth - (markerWidth / 2.0), 0.0, Math.Max(0.0, totalWidth - markerWidth));
            return new Thickness(left, 0, 0, 0);
        }

        return new Thickness(0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
