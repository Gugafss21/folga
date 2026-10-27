using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Folga.App.ViewModels;

/// <summary>Âmbar para núcleo do jogo, cinza para núcleo do fundo.</summary>
public sealed class CoreBrushConverter : IValueConverter
{
    public static CoreBrushConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.FindResource(value is true ? "TextTertiaryBrush" : "AccentBrush");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>[uso 0-100, altura máxima] → altura em px.</summary>
public sealed class PercentToHeightConverter : IMultiValueConverter
{
    public static PercentToHeightConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var usage = values[0] is double u ? u : 0;
        var max = double.TryParse(values[1]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0;
        return Math.Max(2, max * Math.Clamp(usage, 0, 100) / 100);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public static NotNullToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
