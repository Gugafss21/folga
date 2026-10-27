using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Folga.App.Services;
using Folga.Core.Profiles;

namespace Folga.App.ViewModels;

public sealed record NavPage(string Title, ObservableObject ViewModel, string Hint);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Agora = new AgoraViewModel(services);
        Jogos = new JogosViewModel(services);
        SegundoPlano = new SegundoPlanoViewModel(services);
        Pages =
        [
            new("Agora", Agora, "O jogo detectado, o botão Boost, CPU e RAM ao vivo e o mapa de núcleos."),
            new("Jogos", Jogos, "Lista de jogos que o Folga reconhece e o que fazer quando cada um abre."),
            new("Segundo plano", SegundoPlano, "Regras para navegador, Discord e launchers enquanto um jogo está aberto, e o estado real de cada um."),
        ];
        _selectedPage = Pages[0];
    }

    public AgoraViewModel Agora { get; }
    public JogosViewModel Jogos { get; }
    public SegundoPlanoViewModel SegundoPlano { get; }
    public IReadOnlyList<NavPage> Pages { get; }

    public string Version { get; } = "v" + typeof(MainViewModel).Assembly.GetName().Version?.ToString(2);

    [ObservableProperty]
    private NavPage _selectedPage;

    /// <summary>A janela está à vista (não escondida na bandeja nem minimizada).</summary>
    [ObservableProperty]
    private bool _windowVisible;

    partial void OnSelectedPageChanged(NavPage value) => UpdateVisibility();
    partial void OnWindowVisibleChanged(bool value) => UpdateVisibility();

    private void UpdateVisibility()
    {
        Agora.IsVisible = WindowVisible && SelectedPage.ViewModel == Agora;
        SegundoPlano.IsVisible = WindowVisible && SelectedPage.ViewModel == SegundoPlano;
    }

    [RelayCommand]
    private void RestoreAll()
    {
        var answer = MessageBox.Show(
            "Desfazer todas as alterações (inclusive a prioridade gravada no registro para jogos com anti-cheat) e desligar o boost?",
            "Restaurar tudo", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        _services.Engine.RestoreAll();
        _services.SaveEngineConfig();
    }

    /// <summary>Salva uma configuração editada numa tela e reaplica no motor.</summary>
    public static void Commit(AppServices services, Action<FolgaConfig> edit)
    {
        var config = ConfigStore.Clone(services.Engine.Config);
        edit(config);
        services.Store.Save(config);
        services.Engine.ApplyConfig(config);
    }
}
