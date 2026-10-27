using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Folga.App.Views;

public partial class OverlayView : UserControl
{
    public OverlayView() => InitializeComponent();

    // A tabela mostra todas as linhas e não rola; sem isto, ela engoliria a roda do mouse e a página não rolaria.
    private void PassWheelToPage(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        ((UIElement)((FrameworkElement)sender).Parent).RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
    }
}
