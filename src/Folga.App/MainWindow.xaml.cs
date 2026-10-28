using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Folga.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => SyncVisibility();
        StateChanged += (_, _) => SyncVisibility();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is INotifyPropertyChanged vm) vm.PropertyChanged += OnViewModelChanged;
        };
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += (_, _) => FlipLogo();
    }

    /// <summary>
    /// Fechar destrói a janela (o Folga fica na bandeja): antes, confirma a edição de célula que estiver aberta
    /// numa tabela, senão o que foi digitado se perde sem aviso.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        foreach (var grid in Descendants<DataGrid>(this))
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
            grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        }
        base.OnClosing(e);
    }

    /// <summary>Solta o view model (que vive na bandeja), para esta janela poder sair da memória.</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is INotifyPropertyChanged vm) vm.PropertyChanged -= OnViewModelChanged;
        base.OnClosed(e);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>
    /// Ao abrir, a bola do logo gira como uma moeda (escala horizontal 0 → 1 com rebote, cara de 3D sem 3D) e
    /// a tela entra com fade. Toca uma vez e para: nada fica animando parado.
    /// </summary>
    private void FlipLogo()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(520));
        LogoFlip.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut } });
        OnViewModelChanged(this, new PropertyChangedEventArgs(nameof(ViewModels.MainViewModel.SelectedPage)));
    }

    private void SyncVisibility()
    {
        if (DataContext is ViewModels.MainViewModel vm) vm.WindowVisible = IsVisible && WindowState != WindowState.Minimized;
    }

    /// <summary>Troca de tela: a nova entra com um fade e um deslize curtos (0,18 s), sem custo depois.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.MainViewModel.SelectedPage)) return;
        var ease = (IEasingFunction)FindResource("EaseOut");
        var duration = (Duration)FindResource("MediumDuration");
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        PageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
    }

    /// <summary>Barra de título escura, da cor do menu (Windows 11; no 10 fica só escura).</summary>
    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int caption = 0x00141212; // COLORREF (BGR) de #121214, o fundo do menu
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_CAPTION_COLOR = 35;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
