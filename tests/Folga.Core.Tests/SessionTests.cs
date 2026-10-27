using Folga.Core.Engine;
using Folga.Core.Journal;
using Folga.Core.Processes;
using Folga.Core.Profiles;
using Folga.Core.Telemetry;

namespace Folga.Core.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Histograma_da_partida_inteira()
    {
        var h = new FrameTimeHistogram();
        for (var i = 0; i < 980; i++) h.Add(10);   // 100 FPS
        for (var i = 0; i < 20; i++) h.Add(60);    // 2% de travadas acima de 50 ms

        var s = h.Summary();
        Assert.Equal(1000, s.Frames);
        Assert.Equal(20, s.Hitches);
        Assert.Equal(1000 * 1000 / (980 * 10 + 20 * 60d), s.AverageFps!.Value, 3);
        Assert.Equal(1000 / 60.05, s.OnePercentLow!.Value, 1);       // os 1% piores estão nas travadas
        Assert.Equal(20 / ((980 * 10 + 20 * 60d) / 60_000), s.HitchesPerMinute, 3);
    }

    [Fact]
    public void Histograma_com_poucos_quadros_nao_inventa_low()
    {
        var h = new FrameTimeHistogram();
        for (var i = 0; i < 50; i++) h.Add(16.7);
        h.Add(0);    // ignorado
        h.Add(900);  // acima do limite: vai para a última faixa, sem estourar
        var s = h.Summary();
        Assert.Equal(51, s.Frames);
        Assert.Null(s.OnePercentLow);
        Assert.Equal(1, s.Hitches);
    }

    private static SessionMetrics Metrics(double minutes, long frames, double fps, double low, long hitches, double? ping = 30)
    {
        var start = new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
        var frameMs = frames * 1000 / fps;
        return new SessionMetrics(start, start.AddMinutes(minutes), new FrameSummary(frames, frameMs, fps, low, low * 0.8, hitches), ping, 60, 95);
    }

    [Fact]
    public void Partida_curta_ou_sem_FPS_nao_entra()
    {
        var log = new SessionLog(_dir.File("sessions.json"));
        Assert.Null(log.Add("Arc Raiders", true, Metrics(1.5, 9000, 100, 70, 0)));   // menos de 2 min
        Assert.Null(log.Add("Arc Raiders", true, Metrics(30, 0, 1, 0, 0)));         // sem admin: nenhum quadro medido
        Assert.NotNull(log.Add("Arc Raiders", true, Metrics(30, 180_000, 100, 70, 12)));
        Assert.Single(new SessionLog(_dir.File("sessions.json")).All); // gravado em disco
    }

    [Fact]
    public void Comparacao_pondera_pelo_tempo_de_jogo()
    {
        var log = new SessionLog(_dir.File("sessions.json"));
        log.Add("Arc Raiders", true, Metrics(30, 180_000, 100, 80, 30));
        log.Add("Arc Raiders", true, Metrics(10, 60_000, 100, 60, 0));
        log.Add("Arc Raiders", false, Metrics(20, 120_000, 100, 50, 60));

        var c = Assert.Single(SessionLog.Compare(log.All));
        Assert.Equal(2, c.Boost!.Sessions);
        Assert.Equal(75, c.Boost.OnePercentLow!.Value, 6);   // (80*30 + 60*10) / 40: a partida longa pesa mais
        Assert.Equal(100, c.Boost.AverageFps!.Value, 6);
        Assert.Equal(0.75, c.Boost.HitchesPerMinute, 6);     // 30 travadas em 40 min
        Assert.Equal(3, c.Plain!.HitchesPerMinute, 6);       // 60 em 20 min
    }

    [Fact]
    public void Historico_guarda_as_ultimas_300_e_remove()
    {
        var log = new SessionLog(_dir.File("sessions.json"));
        for (var i = 0; i < SessionLog.Keep + 5; i++) log.Add("Jogo", i % 2 == 0, Metrics(5, 30_000, 100, 70, 0));
        Assert.Equal(SessionLog.Keep, log.All.Count);

        var first = log.All[0].Id;
        log.Remove(first);
        Assert.DoesNotContain(log.All, r => r.Id == first);
        Assert.Equal(SessionLog.Keep - 1, new SessionLog(_dir.File("sessions.json")).All.Count);
    }

    [Fact]
    public void So_medir_reconhece_o_jogo_sem_mexer_em_nada()
    {
        var control = new FakeProcessControl();
        var watcher = new FakeWatcher();
        var network = new FakeNetwork();
        var chrome = control.Add(100, "chrome.exe");
        var config = new FolgaConfig
        {
            MeasureOnly = true,
            Games = [new GameProfile { Name = "Jogo", Executables = ["jogo.exe"] }],
            Background = [new BackgroundRule { Name = "Chrome", Executables = ["chrome.exe"] }],
        };
        using var engine = new BoostEngine(config, TopologyTests.Ryzen5500X3D(), control, new FakeIfeo(), new FakeMemory(),
            new ChangeJournal(_dir.File("journal.json")), watcher, network);
        engine.Start();

        var game = control.Add(10, "jogo.exe");
        watcher.RaiseStarted(10, "jogo.exe");

        var state = engine.GetState();
        Assert.True(state.SessionActive);   // a telemetria acompanha o jogo
        Assert.True(state.MeasureOnly);
        Assert.Equal(0x20L, game.State[ChangeKind.Priority][0]);   // prioridade intacta
        Assert.Equal(0x20L, chrome.State[ChangeKind.Priority][0]); // fundo intacto
        Assert.Equal(0, state.HeldProcesses);
        Assert.Empty(network.Begun);

        engine.SetMeasureOnly(false); // no meio da partida: o boost entra na hora
        Assert.Equal(0x80L, game.State[ChangeKind.Priority][0]);
        Assert.Equal(0x4000L, chrome.State[ChangeKind.Priority][0]);
        Assert.False(engine.GetState().MeasureOnly);
    }
}
