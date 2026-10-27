using System.Windows;

namespace Folga.App.Views;

/// <summary>
/// Dica de uma coluna de tabela. O estilo Table (Theme/Controls.xaml) mostra o texto ao passar o mouse
/// no cabeçalho e em cada célula da coluna, sem repetir o tooltip em todo lugar.
/// </summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Hint), new PropertyMetadata(null));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);
}
