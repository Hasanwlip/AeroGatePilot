using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AeroGatePilot.Infrastructure.Network;

namespace AeroGatePilot.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (value is string s)
            flag = !string.IsNullOrWhiteSpace(s);
        else if (value is int i)
            flag = i != 0;
        else if (value is not bool)
            flag = value is not null;
        return flag ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) ^ Invert;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Binds RadioButtons to an enum: IsChecked="{Binding Mode, Converter={StaticResource EnumMatch}, ConverterParameter=Both}".</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string name
            ? targetType == typeof(string) ? name : Enum.Parse(targetType, name)
            : Binding.DoNothing;
}

public sealed class DiagnosticBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DiagnosticStatus.Ok => Application.Current.Resources["SuccessBrush"],
        DiagnosticStatus.Warning => Application.Current.Resources["WarningBrush"],
        DiagnosticStatus.Error => Application.Current.Resources["DangerBrush"],
        _ => Application.Current.Resources["AccentBrush"],
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DiagnosticGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DiagnosticStatus.Ok => "\uE73E",
        DiagnosticStatus.Warning => "\uE7BA",
        DiagnosticStatus.Error => "\uE711",
        _ => "\uE946",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is string s && s.StartsWith('#') && s.Length is 4 or 7 or 9)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
        }
        catch (FormatException)
        {
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
