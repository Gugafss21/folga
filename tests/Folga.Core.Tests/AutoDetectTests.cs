using Folga.Core.Detection;
using Folga.Core.Engine;
using Folga.Core.Journal;
using Folga.Core.Processes;
using Folga.Core.Profiles;
using Microsoft.Extensions.Time.Testing;

namespace Folga.Core.Tests;

internal sealed class FakeForeground : IForegroundSource
{
    public ForegroundWindow? Window { get; set; }
    public ForegroundWindow? Read() => Window;
}

internal sealed class FakeProbe(double fps) : IPresentProbe
{
    public double Fps { get; set; } = fps;
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

public sealed class AutoDetectTests : IDisposable
{
    private static readonly TimeSpan Confirm = TimeSpan.FromSeconds(10);

    private readonly TempDir _dir = new();
    private readonly FakeForeground _foreground = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<FakeProbe> _probes = [];
    private readonly List<DetectedGame> _detected = [];
    private FolgaConfig _config = DefaultConfig.Create();
    private bool _session;
    private double? _probeFps = 60; // null = sem admin, não dá para medir o DirectX

    public void Dispose() => _dir.Dispose();

    private AutoGameDetector Detector()
    {
        var detector = new AutoGameDetector(_foreground, _ => Probe(), () => _config, () => _session, _time);
        detector.Detected += _detected.Add;
        return detector;
    }

    private FakeProbe? Probe()
    {
        if (_probeFps is not { } fps) return null;
        var probe = new FakeProbe(fps);
        _probes.Add(probe);
        return probe;
    }

    private static ForegroundWindow Window(string exe = "Hollow.exe", string title = "Hollow Knight", int pid = 500, bool fullscreen = true, bool system = false) =>
        new(pid, exe, title, fullscreen, system);

    /// <summary>Primeiro tick cria o candidato; depois espera o tempo de confirmação e verifica de novo.</summary>
    private static void RunFor(AutoGameDetector detector, FakeTimeProvider time, TimeSpan span)
    {
        detector.Tick();
        time.Advance(span);
        detector.Tick();
    }

    [Fact]
    public void Tela_cheia_com_DirectX_por_10_s_vira_jogo_uma_vez()
    {
        using var detector = Detector();
        _foreground.Window = Window();

        detector.Tick();
        _time.Advance(Confirm - TimeSpan.FromSeconds(1));
        detector.Tick();
        Assert.Empty(_detected); // ainda não deu o tempo

        _time.Advance(TimeSpan.FromSeconds(1));
        detector.Tick();
        var game = Assert.Single(_detected);
        Assert.Equal(("Hollow.exe", "Hollow Knight", 60d), (game.ExeName, game.Name, game.Fps));
        Assert.True(_probes[0].Disposed); // a medição curta é desligada ao decidir

        _time.Advance(Confirm);
        detector.Tick();
        detector.Tick();
        Assert.Single(_detected); // mesmo processo não dispara de novo
    }

    [Fact]
    public void Trocar_de_janela_recomeca_a_contagem()
    {
        using var detector = Detector();
        _foreground.Window = Window();
        detector.Tick();
        _time.Advance(TimeSpan.FromSeconds(6));
        _foreground.Window = Window(exe: "notepad.exe", title: "Bloco", pid: 600, fullscreen: false); // alt-tab
        detector.Tick();
        _foreground.Window = Window();
        detector.Tick();
        _time.Advance(TimeSpan.FromSeconds(6));
        detector.Tick();

        Assert.Empty(_detected);
        Assert.True(_probes[0].Disposed);
    }

    [Theory]
    [InlineData("chrome.exe", true, false)]     // exclusão padrão (YouTube em tela cheia)
    [InlineData("vlc.exe", true, false)]        // player de vídeo
    [InlineData("OneDrive.exe", true, false)]   // tem regra de segundo plano
    [InlineData("Hollow.exe", false, false)]    // maximizada com barra de título
    [InlineData("LockApp.exe", true, true)]     // .exe na pasta do Windows
    public void Excluidos_janelas_comuns_e_do_Windows_nao_viram_jogo(string exe, bool fullscreen, bool system)
    {
        using var detector = Detector();
        _foreground.Window = Window(exe: exe, fullscreen: fullscreen, system: system);
        RunFor(detector, _time, Confirm * 4);

        Assert.Empty(_detected);
        Assert.Empty(_probes); // nem chega a medir
    }

    [Fact]
    public void Tela_cheia_sem_desenhar_como_jogo_e_descartada()
    {
        _probeFps = 0; // ex.: um app de slides em tela cheia
        using var detector = Detector();
        _foreground.Window = Window(exe: "Slides.exe");

        RunFor(detector, _time, Confirm);
        Assert.Empty(_detected);
        Assert.False(_probes[0].Disposed); // ainda pode ser tela de carregamento

        _time.Advance(Confirm * 2);
        detector.Tick();
        Assert.Empty(_detected);
        Assert.True(_probes[0].Disposed); // desistiu

        _time.Advance(Confirm * 4);
        detector.Tick();
        Assert.Single(_probes); // não volta a medir o mesmo processo
    }

    [Fact]
    public void Tela_de_carregamento_e_aceita_quando_o_FPS_sobe()
    {
        using var detector = Detector();
        _foreground.Window = Window();
        detector.Tick();
        _probes[0].Fps = 5;
        _time.Advance(Confirm);
        detector.Tick();
        Assert.Empty(_detected);

        _probes[0].Fps = 144;
        _time.Advance(TimeSpan.FromSeconds(5));
        detector.Tick();
        Assert.Equal(144d, Assert.Single(_detected).Fps);
    }

    [Fact]
    public void Sem_admin_vale_so_a_tela_cheia()
    {
        _probeFps = null;
        using var detector = Detector();
        _foreground.Window = Window();
        RunFor(detector, _time, Confirm);

        Assert.Null(Assert.Single(_detected).Fps);
    }

    [Fact]
    public void Nao_procura_com_jogo_aberto_boost_desligado_ou_jogo_ja_conhecido()
    {
        using var detector = Detector();
        _foreground.Window = Window();

        _session = true;
        RunFor(detector, _time, Confirm);
        _session = false;

        _config.BoostEnabled = false;
        RunFor(detector, _time, Confirm);
        _config.BoostEnabled = true;

        _config.AutoDetect.Enabled = false;
        RunFor(detector, _time, Confirm);
        _config.AutoDetect.Enabled = true;

        // Perfil desativado: o usuário não quer boost nesse jogo, então não é redetectado.
        _config.Games.Add(new GameProfile { Name = "Hollow", Executables = ["hollow.exe"], Enabled = false });
        RunFor(detector, _time, Confirm);

        Assert.Empty(_detected);
    }

    [Theory]
    [InlineData("ARC Raiders", "PioneerGame-Win64-Shipping.exe", "ARC Raiders")]
    [InlineData("", "PioneerGame-Win64-Shipping.exe", "PioneerGame-Win64-Shipping")]
    [InlineData("Um título de janela comprido demais para nome de jogo na lista", "Game.exe", "Game")]
    public void Nome_vem_do_titulo_ou_do_exe(string title, string exe, string expected) =>
        Assert.Equal(expected, AutoGameDetector.GameName(Window(exe: exe, title: title)));

    [Fact]
    public void Perfil_detectado_entra_no_motor_e_aplica_na_hora()
    {
        var control = new FakeProcessControl();
        var chrome = control.Add(100, "chrome.exe");
        var game = control.Add(500, "Hollow.exe");
        using var engine = new BoostEngine(DefaultConfig.Create(), TopologyTests.Ryzen5500X3D(), control, new FakeIfeo(), new FakeMemory(),
            new ChangeJournal(_dir.File("journal.json")), new FakeWatcher(), new FakeNetwork(), _time);
        engine.Start();
        Assert.False(engine.GetState().SessionActive);

        engine.AddGameProfile(new GameProfile { Name = "Hollow Knight", Executables = ["Hollow.exe"], AutoDetected = true });

        var state = engine.GetState();
        Assert.Equal("Hollow Knight", Assert.Single(state.Games).ProfileName);
        Assert.Equal(0x80L, game.State[ChangeKind.Priority][0]);       // Alta
        Assert.Equal(0x4000L, chrome.State[ChangeKind.Priority][0]);   // fundo segurado
        Assert.True(engine.Config.FindGame("hollow.exe")!.AutoDetected);
    }

    [Fact]
    public void Config_v2_ganha_a_deteccao_ligada_com_as_exclusoes_padrao()
    {
        var path = _dir.File("config.json");
        File.WriteAllText(path, """{ "version": 2, "games": [ { "name": "X", "executables": ["x.exe"] } ] }""");
        var store = new ConfigStore(path);

        var config = store.Load();
        Assert.Equal(FolgaConfig.CurrentVersion, config.Version);
        Assert.True(config.AutoDetect.Enabled);
        Assert.True(config.IsExcludedFromAutoDetect("CHROME.EXE"));
        Assert.True(config.IsExcludedFromAutoDetect("mpc-hc64.exe"));
        Assert.False(config.IsExcludedFromAutoDetect("x.exe"));

        config.AutoDetect.Exclusions = ["so-isto.exe"];
        store.Save(config);
        Assert.Equal(["so-isto.exe"], store.Load().AutoDetect.Exclusions); // a lista salva substitui a padrão
    }
}
