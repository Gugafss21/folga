using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Folga.App.Services;
using Folga.Core.Profiles;

namespace Folga.App.ViewModels;

/// <summary>Uma tela no menu lateral. Icon: código do caractere na fonte de ícones do Windows; Group: título do bloco no menu.</summary>
public sealed record NavPage(string Group, string Icon, string Title, ObservableObject ViewModel, string Hint);

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
        Hardware = new HardwareViewModel(services.Telemetry.GpuDetails);
        Limpeza = new LimpezaViewModel(() => services.Engine.GetState().SessionActive);
        Sistema = new SistemaViewModel(services);
        Pages =
        [
            new("Jogo", "\uEC4A", "Agora", Agora, "O jogo aberto agora, o botão do boost, FPS, CPU, RAM e o uso de cada núcleo."),
            new("Jogo", "\uE7FC", "Jogos", Jogos, "Os jogos que o Folga reconhece e o que ele faz quando cada um abre."),
            new("Jogo", "\uE81C", "Sessões", Sessoes, "Cada partida medida, a comparação com boost × sem boost e de onde vêm as travadas."),
            new("Ajustes", "\uE71D", "Segundo plano", SegundoPlano, "O que acontece com navegador, Discord e launchers enquanto você joga."),
            new("Ajustes", "\uE839", "Rede", Rede, "Quem está usando a internet agora e o que o Folga segura durante o jogo."),
            new("Ajustes", "\uE890", "Overlay", Overlay, "Os números desenhados por cima do jogo (pelo RTSS): quais, onde e com que cores."),
            new("Ajustes", "\uE713", "Sistema", Sistema, "Ajustes do Windows que mexem nos jogos, e a instalação e as atualizações do Folga."),
            new("Seu PC", "\uE7F4", "Hardware", Hardware, "A ficha do seu PC, com o padrão de fábrica ao lado do que está valendo."),
            new("Seu PC", "\uE74D", "Limpeza", Limpeza, "Libera espaço apagando só caches e temporários, nunca arquivos seus."),
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
    public HardwareViewModel Hardware { get; }
    public LimpezaViewModel Limpeza { get; }
    public SistemaViewModel Sistema { get; }
    public IReadOnlyList<NavPage> Pages { get; }

    public string Version { get; } = "v" + typeof(MainViewModel).Assembly.GetName().Version?.ToString(3);

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
        Hardware.IsVisible = WindowVisible && SelectedPage.ViewModel == Hardware;
        Limpeza.IsVisible = WindowVisible && SelectedPage.ViewModel == Limpeza;
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
