using System;
using System.Windows;
using System.Windows.Data;

namespace DemoUI.Converters
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var rv = Visibility.Visible;
            try
            {
                var reverse = (parameter != null && parameter.ToString().ToLower() == "true");
                var x = value != null && bool.Parse(value.ToString());
                rv = x
                    ? ((!reverse) ? Visibility.Visible : Visibility.Collapsed)
                    : ((!reverse) ? Visibility.Collapsed : Visibility.Visible);
            }
            catch (Exception)
            {
            }
            return rv;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value;
        }
    }
}
