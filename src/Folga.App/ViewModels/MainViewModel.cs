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
        Rede = new RedeViewModel(services);
        Overlay = new OverlayViewModel(services);
        Sessoes = new SessoesViewModel(services);
        Sistema = new SistemaViewModel(services);
        Pages =
        [
            new("Agora", Agora, "O jogo detectado, o botão Boost, CPU e RAM ao vivo e o mapa de núcleos."),
            new("Jogos", Jogos, "Lista de jogos que o Folga reconhece e o que fazer quando cada um abre."),
            new("Segundo plano", SegundoPlano, "Regras para navegador, Discord e launchers enquanto um jogo está aberto, e o estado real de cada um."),
            new("Rede", Rede, "Quem está usando download e upload agora, e o que o Folga faz na rede durante o jogo."),
            new("Overlay", Overlay, "Os números do jogo desenhados por cima do jogo pelo RTSS: o que aparece, nomes, tamanho, cores e canto."),
            new("Sessões", Sessoes, "Cada partida medida (FPS, 1% low, travadas, ping) e a comparação com boost contra sem boost (modo Só medir), para ver o que o Folga muda no seu PC."),
            new("Sistema", Sistema, "Ajustes do próprio Windows: arquivo de paginação, memória compactada, Modo de Jogo, otimizações de janela, capturas do Game Bar, agendamento de GPU e plano de energia durante o jogo. Avisa quando precisa reiniciar."),
        ];
        _selectedPage = Pages[0];
        services.Engine.StateChanged += () => Application.Current?.Dispatcher.BeginInvoke(RefreshBoost);
        RefreshBoost();
    }

    /// <summary>Estado do boost no rodapé do menu, visível em qualquer tela.</summary>
    [ObservableProperty] private string _boostStatus = "";

    /// <summary>"Active", "Waiting" ou "Off": escolhe a cor do ponto ao lado do estado.</summary>
    [ObservableProperty] private string _boostKind = "Off";

    private void RefreshBoost()
    {
        var state = _services.Engine.GetState();
        var measuring = state.MeasureOnly ? " · só medindo" : "";
        (BoostKind, BoostStatus) = !state.Enabled ? ("Off", "Boost desligado")
            : state.SessionActive ? ("Active", state.Games[0].ProfileName + measuring)
            : ("Waiting", (state.MeasureOnly ? "Só medir" : "Esperando um jogo"));
    }

    public AgoraViewModel Agora { get; }
    public JogosViewModel Jogos { get; }
    public SegundoPlanoViewModel SegundoPlano { get; }
    public RedeViewModel Rede { get; }
    public OverlayViewModel Overlay { get; }
    public SessoesViewModel Sessoes { get; }
    public SistemaViewModel Sistema { get; }
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
        Rede.IsVisible = WindowVisible && SelectedPage.ViewModel == Rede;
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
