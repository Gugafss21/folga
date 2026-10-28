using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Folga.App.ViewModels;

/// <summary>[uso 0-100, altura máxima] → altura em px. Mínimo de 2 px (a barra nunca some), menos com o parâmetro "0".</summary>
public sealed class PercentToHeightConverter : IMultiValueConverter
{
    public static PercentToHeightConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var usage = values[0] is double u ? u : 0;
        var max = double.TryParse(values[1]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0;
        return Math.Max(parameter as string == "0" ? 0 : 2, max * Math.Clamp(usage, 0, 100) / 100);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
}

/// <summary>"RRGGBB" → pincel (amostra de cor ao lado do campo).</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static HexToBrushConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => Brush(value as string);

    public static SolidColorBrush Brush(string? hex)
    {
        var c = Folga.Core.Overlay.OverlayFormatter.NormalizeColor(hex);
        var brush = new SolidColorBrush(Color.FromRgb(System.Convert.ToByte(c[..2], 16), System.Convert.ToByte(c[2..4], 16), System.Convert.ToByte(c[4..], 16)));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>HasItems da tabela → a mensagem de "vazio" aparece só sem linhas.</summary>
public sealed class NoItemsToVisibilityConverter : IValueConverter
{
    public static NoItemsToVisibilityConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public static NotNullToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
