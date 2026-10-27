using System.Windows;
using System.Windows.Input;

namespace Folga.App.Views;

/// <summary>
/// Para tabelas dentro de uma página que rola: a tabela mostra todas as linhas e não rola, e sem isto ela
/// engoliria a roda do mouse e a página não rolaria com o mouse em cima dela.
/// </summary>
public static class Wheel
{
    public static readonly DependencyProperty PassToPageProperty = DependencyProperty.RegisterAttached(
        "PassToPage", typeof(bool), typeof(Wheel), new PropertyMetadata(false, OnChanged));

    public static bool GetPassToPage(DependencyObject element) => (bool)element.GetValue(PassToPageProperty);
    public static void SetPassToPage(DependencyObject element, bool value) => element.SetValue(PassToPageProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue) element.PreviewMouseWheel += Pass;
        else element.PreviewMouseWheel -= Pass;
    }

    private static void Pass(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement { Parent: UIElement parent }) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent });
    }
}
