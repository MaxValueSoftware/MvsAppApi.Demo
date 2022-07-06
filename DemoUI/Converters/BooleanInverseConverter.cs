using System;
using System.Globalization;
using System.Windows.Data;

namespace DemoUI.Converters
{
    public class BooleanInverseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (targetType != typeof(bool))
                throw new InvalidOperationException("Cannot invert type. Must be boolean");

            return (value != null) && !(bool)value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (targetType != typeof(bool))
                throw new InvalidOperationException("Cannot invert type. Must be boolean");

            return (value != null) && !(bool)value;
        }
    }
}