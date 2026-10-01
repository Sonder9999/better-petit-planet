using System;
using System.Globalization;
using System.Windows.Data;

namespace BetterPetitPlanet.View.Converters;

public sealed class MillisecondsToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double ms = 0;
        if (value is double d)
        {
            ms = d;
        }
        else if (value is int i)
        {
            ms = i;
        }
        else if (value is long l)
        {
            ms = l;
        }
        else
        {
            return "00:00";
        }

        if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0)
        {
            return "00:00";
        }

        var time = TimeSpan.FromMilliseconds(ms);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
