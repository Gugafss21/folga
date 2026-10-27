using System.ComponentModel;
using System.Windows;

namespace Folga.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => SyncVisibility();
        StateChanged += (_, _) => SyncVisibility();
    }

    private void SyncVisibility()
    {
        if (DataContext is ViewModels.MainViewModel vm) vm.WindowVisible = IsVisible && WindowState != WindowState.Minimized;
    }

    /// <summary>Ligado pelo "Sair" da bandeja; sem ele, fechar a janela só esconde.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Fechar a janela só esconde: o Folga continua na bandeja. Para sair, use o menu da bandeja.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }
}
