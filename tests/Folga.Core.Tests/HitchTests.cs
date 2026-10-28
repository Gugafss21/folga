using Folga.Core.Telemetry;

namespace Folga.Core.Tests;

public sealed class HitchTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static SystemSample Calm => new(GameDiskMBps: 2, HardFaultMBps: 0.5, AvailableRamMb: 6000, VramPercent: 80, CpuPercent: 50, OtherCpuPercent: 5, AfterLoading: false);

    [Fact]
    public void Travadas_que_vem_com_o_disco_apontam_o_disco()
    {
        var p = new HitchProfiler(totalRamMb: 16_384);
        for (var i = 0; i < 540; i++) p.Add(Calm, i % 60 == 0 ? 1 : 0);          // 9 travadas em 9 min tranquilos
        for (var i = 0; i < 60; i++) p.Add(Calm with { GameDiskMBps = 120 }, 1); // 60 travadas no minuto lendo do disco

        var disk = p.Factors().Single(f => f.Cause == HitchCause.GameDiskRead);
        Assert.Equal(600, disk.Seconds);
        Assert.Equal(69, disk.Hitches);
        Assert.Equal(60, disk.ConditionHitches);
        Assert.Equal(0.1, disk.ShareOfTime, 3);
        Assert.Equal(60 / 69d, disk.ShareOfHitches, 3);
        Assert.Equal(60 / (9 / 540d) / 60, disk.Ratio!.Value, 3);              // 1 por segundo contra 1 por minuto
        Assert.Equal((60 * 120 + 9 * 2) / 69d, disk.AverageAtHitch!.Value, 3);
        Assert.Equal(2, disk.AverageCalm!.Value, 3);

        var likely = HitchVerdict.Likely(p.Factors());
        Assert.Equal([HitchCause.GameDiskRead], likely.Select(f => f.Cause));
    }

    [Fact]
    public void Medida_que_a_fonte_nao_informa_fica_de_fora()
    {
        var p = new HitchProfiler(16_384);
        p.Add(Calm with { VramPercent = null, OtherCpuPercent = null }, 1);
        var causes = p.Factors().Select(f => f.Cause).ToList();
        Assert.DoesNotContain(HitchCause.VramFull, causes);
        Assert.DoesNotContain(HitchCause.OtherCpu, causes);
        Assert.Contains(HitchCause.AfterLoading, causes); // sempre medido
    }

    [Fact]
    public void Pouca_RAM_e_10_por_cento_com_minimo_de_1_GB()
    {
        Assert.Equal(1638.4, new HitchProfiler(16_384).LowRamThresholdMb, 3);
        Assert.Equal(1024, new HitchProfiler(4_096).LowRamThresholdMb);
    }

    [Fact]
    public void Chance_sem_travada_fora_ou_sem_tempo_dentro()
    {
        var p = new HitchProfiler(16_384);
        for (var i = 0; i < 100; i++) p.Add(Calm, 0);
        for (var i = 0; i < 10; i++) p.Add(Calm with { VramPercent = 99 }, 2);
        var factors = p.Factors();
        Assert.Equal(double.PositiveInfinity, factors.Single(f => f.Cause == HitchCause.VramFull).Ratio);
        Assert.Null(factors.Single(f => f.Cause == HitchCause.CpuFull).Ratio);   // nunca no limite
        Assert.True(HitchVerdict.IsLikely(factors.Single(f => f.Cause == HitchCause.VramFull)));
    }

    [Fact]
    public void Com_poucas_travadas_nao_aponta_causa()
    {
        var p = new HitchProfiler(16_384);
        for (var i = 0; i < 300; i++) p.Add(Calm, 0);
        for (var i = 0; i < 5; i++) p.Add(Calm with { GameDiskMBps = 200 }, 1);
        Assert.Empty(HitchVerdict.Likely(p.Factors()));
    }

    [Fact]
    public void Gravador_conta_por_segundo_e_marca_o_minuto_depois_de_carregar()
    {
        var r = new HitchRecorder(16_384);
        var t = new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);
        long total = 0;
        // Menu e partida: 120 s desenhando, 1 travada por segundo nos primeiros 10 s, nenhuma depois.
        for (var i = 0; i < 120; i++)
        {
            if (i < 10) total++;
            r.Tick(t.AddSeconds(i), drawing: true, total, Calm);
        }
        // Carregamento: 30 s sem desenhar não entram.
        for (var i = 120; i < 150; i++) r.Tick(t.AddSeconds(i), drawing: false, total, Calm);
        // Volta: a 1ª travada (o próprio fim do carregamento) e mais 4 no primeiro minuto.
        for (var i = 150; i < 240; i++)
        {
            if (i is 150 or 160 or 170 or 180 or 190) total++;
            r.Tick(t.AddSeconds(i), drawing: true, total, Calm);
        }

        Assert.Equal(210, r.Seconds);
        var loading = r.Factors().Single(f => f.Cause == HitchCause.AfterLoading);
        Assert.Equal(15, loading.Hitches);
        Assert.Equal(15, loading.ConditionHitches);
        Assert.Equal(120, loading.ConditionSeconds);   // 60 s no começo + 60 s depois do carregamento
    }

    [Fact]
    public void Gravador_recomeca_a_conta_quando_a_cadeia_principal_troca()
    {
        var r = new HitchRecorder(16_384);
        var t = DateTimeOffset.UnixEpoch;
        r.Tick(t, true, 5, Calm);
        r.Tick(t.AddSeconds(1), true, 3, Calm);   // outra cadeia virou a principal: não vira travada negativa
        r.Tick(t.AddSeconds(2), true, 6, Calm);   // e as 3 dela depois da troca contam
        Assert.Equal(8, r.Factors().Single(f => f.Cause == HitchCause.AfterLoading).Hitches);
    }

    [Fact]
    public void Partidas_somam_e_o_diagnostico_fica_gravado()
    {
        var p = new HitchProfiler(16_384);
        for (var i = 0; i < 200; i++) p.Add(Calm with { HardFaultMBps = i < 20 ? 30 : 0.5 }, i < 20 ? 1 : 0);

        var start = new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);
        var metrics = new SessionMetrics(start, start.AddMinutes(10), new FrameSummary(60_000, 600_000, 100, 60, 40, 20), 25, 40, 90, p.Factors());
        var log = new SessionLog(_dir.File("sessions.json"));
        log.Add("Arc Raiders", true, metrics);
        log.Add("Arc Raiders", false, metrics);

        var reloaded = new SessionLog(_dir.File("sessions.json")).All;
        var combined = HitchVerdict.Combine(reloaded.Select(r => r.HitchFactors));
        var faults = combined.Single(f => f.Cause == HitchCause.HardFaults);
        Assert.Equal(400, faults.Seconds);
        Assert.Equal(40, faults.ConditionHitches);
        Assert.Equal(30, faults.AverageAtHitch!.Value, 3);
        Assert.Equal([HitchCause.HardFaults], HitchVerdict.Likely(combined).Select(f => f.Cause));
    }

    [Fact]
    public void Partida_antiga_sem_diagnostico_continua_legivel()
    {
        File.WriteAllText(_dir.File("sessions.json"), """[{"game":"Arc Raiders","boost":true,"frames":1000,"frameMs":60000}]""");
        var record = Assert.Single(new SessionLog(_dir.File("sessions.json")).All);
        Assert.Null(record.HitchFactors);
        Assert.Empty(HitchVerdict.Combine([record.HitchFactors]));
    }

    [Fact]
    public void Leitura_real_do_PC()
    {
        using var probe = new WindowsSystemProbe([Environment.ProcessId]);
        Assert.True(probe.TotalRamMb > 1000);
        var buffer = new byte[4 * 1024 * 1024];
        File.WriteAllBytes(_dir.File("leitura.bin"), buffer);
        _ = File.ReadAllBytes(_dir.File("leitura.bin"));
        SpinWait.SpinUntil(() => false, 50);

        var s = probe.Read(vramPercent: 70);
        Assert.NotNull(s.GameDiskMBps);
        Assert.True(s.GameDiskMBps > 0);
        Assert.InRange(s.AvailableRamMb!.Value, 1, probe.TotalRamMb);
        Assert.InRange(s.CpuPercent!.Value, 0, 100);
        Assert.InRange(s.OtherCpuPercent!.Value, 0, 100);
        Assert.Equal(70, s.VramPercent);
        Assert.False(s.AfterLoading);
    }
}
