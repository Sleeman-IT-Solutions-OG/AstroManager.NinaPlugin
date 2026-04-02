using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Converts bool to Color/Brush for selection state styling
    /// </summary>
    public class BoolToColorConverter : IValueConverter
    {
        public string TrueColor { get; set; } = "#0E639C";
        public string FalseColor { get; set; } = "#3C3C3C";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var isTrue = value is bool b && b;
            var colorStr = isTrue ? TrueColor : FalseColor;
            
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorStr);
                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(System.Windows.Media.Colors.Gray);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts bool to Visibility (true = Visible, false = Collapsed)
    /// Use ConverterParameter=Invert to reverse the logic
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var invert = parameter is string str && str.Equals("Invert", StringComparison.OrdinalIgnoreCase);
            
            if (value is bool boolValue)
            {
                if (invert) boolValue = !boolValue;
                return boolValue ? Visibility.Visible : Visibility.Collapsed;
            }
            return invert ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var invert = parameter is string str && str.Equals("Invert", StringComparison.OrdinalIgnoreCase);
            
            if (value is Visibility visibility)
            {
                var result = visibility == Visibility.Visible;
                return invert ? !result : result;
            }
            return invert;
        }
    }

    /// <summary>
    /// Converts bool to Visibility (true = Collapsed, false = Visible)
    /// Inverse of BoolToVisibilityConverter
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                return visibility != Visibility.Visible;
            }
            return true;
        }
    }

    /// <summary>
    /// Simple bool inverter (true => false, false => true)
    /// </summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }
    }

    /// <summary>
    /// Converts int to Visibility (0 = Visible, non-zero = Collapsed)
    /// Used for showing empty state messages
    /// </summary>
    public class ZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue)
            {
                return intValue == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts nullable bool to "Yes"/"No"/"-" string
    /// </summary>
    public class BoolToYesNoConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? "Yes" : "No";
            }
            return "-";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string strValue)
            {
                return strValue.Equals("Yes", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
    }

    /// <summary>
    /// Converts GoalCompletionBehaviour string to display-friendly text
    /// </summary>
    public class GoalCompletionBehaviorDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string strValue)
            {
                return strValue switch
                {
                    "Continue" => "Continue",
                    "Stop" => "Stop",
                    "LowerPriority" => "Lower Pri",
                    _ => strValue ?? "-"
                };
            }
            return "-";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts FilterShootingPattern string to display-friendly text
    /// </summary>
    public class FilterShootingPatternDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string strValue)
            {
                return strValue switch
                {
                    "Loop" => "Parallel",
                    "Batch" => "Batch",
                    "Complete" => "Sequential",
                    _ => strValue ?? "-"
                };
            }
            return "-";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts TargetSelectionStrategy string to display-friendly text
    /// </summary>
    public class StrategyDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string strValue)
            {
                return strValue switch
                {
                    "Priority" => "Priority",
                    "Altitude" => "Altitude",
                    "Completion" => "Completion",
                    "MeridianWindow" => "Meridian",
                    _ => strValue ?? "-"
                };
            }
            return "-";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts null or empty string to Visibility.Collapsed, otherwise Visible
    /// </summary>
    public class NullOrEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return Visibility.Collapsed;
            if (value is string str && string.IsNullOrWhiteSpace(str))
                return Visibility.Collapsed;
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Multi-value converter that returns Visible only if ALL bound boolean values are true
    /// </summary>
    public class MultiBoolToVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length == 0)
                return Visibility.Collapsed;

            foreach (var value in values)
            {
                if (value is bool b && !b)
                    return Visibility.Collapsed;
                if (value == DependencyProperty.UnsetValue)
                    return Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
