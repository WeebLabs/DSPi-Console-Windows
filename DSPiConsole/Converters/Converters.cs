using DSPiConsole.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Converters;

/// <summary>
/// Converts boolean to visibility
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool invert = parameter?.ToString() == "Invert";
        bool visible = value is bool b && b;
        if (invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts FilterType to display name
/// </summary>
public class FilterTypeToNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is FilterType ft ? ft.GetDisplayName() : "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts FilterType to short code
/// </summary>
public class FilterTypeToShortNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is FilterType ft ? ft.GetShortName() : "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts Channel to its associated Color
/// </summary>
public class ChannelToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not Channel channel)
            return Colors.White;
        return channel.Color;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts Channel to SolidColorBrush
/// </summary>
public class ChannelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not Channel channel)
            return new SolidColorBrush(Colors.White);
        return new SolidColorBrush(channel.Color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts float to formatted string with specified format
/// </summary>
public class FloatFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not float f) return "";
        string format = parameter as string ?? "F1";
        return f.ToString(format);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is string s && float.TryParse(s, out float result))
            return result;
        return 0f;
    }
}

/// <summary>
/// Converts FilterType to visibility based on whether it has gain
/// </summary>
public class FilterTypeHasGainConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not FilterType ft) return Visibility.Collapsed;
        return ft.HasGain() ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts FilterType to visibility based on whether it has Q
/// </summary>
public class FilterTypeHasQConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not FilterType ft) return Visibility.Collapsed;
        return ft.HasQ() ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts FilterType to visibility based on whether it has frequency
/// </summary>
public class FilterTypeHasFrequencyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not FilterType ft) return Visibility.Collapsed;
        return ft.HasFrequency() ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns opacity based on whether filter is active (not Flat)
/// </summary>
public class FilterTypeToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not FilterType ft) return 0.4;
        return ft != FilterType.Flat ? 1.0 : 0.4;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
