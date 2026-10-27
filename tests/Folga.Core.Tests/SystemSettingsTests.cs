using Folga.Core.Engine;
using Folga.Core.Journal;
using Folga.Core.Processes;
using Folga.Core.Profiles;
using Folga.Core.SystemSettings;

namespace Folga.Core.Tests;

internal sealed class FakePowerPlan : IPowerPlan
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid High = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public Guid Current { get; set; } = Balanced;
    public bool FailSet { get; set; }
    public Guid? Active() => Current;

    public bool SetActive(Guid scheme)
    {
        if (FailSet) return false;
        Current = scheme;
        return true;
    }

    public IReadOnlyList<PowerScheme> List() => [new(Balanced, "Equilibrado"), new(High, "Alto desempenho")];
}

public sealed class SystemSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(@"?:\pagefile.sys", PagefileMode.SystemManaged, "?:", 0, 0)]
    [InlineData(@"c:\pagefile.sys 0 0", PagefileMode.SystemManaged, "C:", 0, 0)]  // como está no PC do usuário
    [InlineData(@"D:\pagefile.sys 8192 16384", PagefileMode.Custom, "D:", 8192, 16384)]
    [InlineData("", PagefileMode.None, "C:", 0, 0)]
    public void Paginacao_lida_do_registro(string entry, PagefileMode mode, string drive, int initial, int maximum) =>
        Assert.Equal(new PagefileSetting(mode, drive, initial, maximum), Pagefile.Parse(entry.Length > 0 ? [entry] : []));

    [Fact]
    public void Paginacao_gravada_no_formato_da_tela_de_memoria_virtual()
    {
        Assert.Equal([@"?:\pagefile.sys"], Pagefile.Format(new PagefileSetting(PagefileMode.SystemManaged, "?:")));
        Assert.Equal([@"C:\pagefile.sys 0 0"], Pagefile.Format(new PagefileSetting(PagefileMode.SystemManaged, "C:")));
        Assert.Equal([@"C:\pagefile.sys 8192 16384"], Pagefile.Format(new PagefileSetting(PagefileMode.Custom, "C:", 8192, 16384)));
        Assert.Equal([@"C:\pagefile.sys 8192 8192"], Pagefile.Format(new PagefileSetting(PagefileMode.Custom, "C:", 8192, 4096))); // máximo nunca menor que o mínimo
        Assert.Empty(Pagefile.Format(new PagefileSetting(PagefileMode.None)));
    }

    [Fact]
    public void Avisos_de_paginacao_para_16_GB()
    {
        const long Gb = 1L << 30;
        var calm = new CommitSnapshot(16 * Gb, 21 * Gb, 9 * Gb);
        var tight = new CommitSnapshot(16 * Gb, 21 * Gb, 14 * Gb);

        Assert.Null(Pagefile.Warning(new PagefileSetting(PagefileMode.SystemManaged), calm));
        Assert.Null(Pagefile.Warning(new PagefileSetting(PagefileMode.Custom, "C:", 8192, 16384), calm));
        Assert.Contains("limite de memória vira os 16 GB", Pagefile.Warning(new PagefileSetting(PagefileMode.None), calm));
        Assert.Contains("vão fechar", Pagefile.Warning(new PagefileSetting(PagefileMode.None), tight));
        Assert.Contains("é pouco", Pagefile.Warning(new PagefileSetting(PagefileMode.Custom, "C:", 1024, 2048), calm));
    }

    [Fact]
    public void Otimizacoes_de_janela_mantem_o_resto_da_string_do_DirectX()
    {
        var settings = GamingSettings.ParseDirectX("SwapEffectUpgradeEnable=1;AutoHDREnable=1;");
        settings["SwapEffectUpgradeEnable"] = "0";
        Assert.Equal("SwapEffectUpgradeEnable=0;AutoHDREnable=1;", GamingSettings.FormatDirectX(settings));

        var empty = GamingSettings.ParseDirectX(null);
        empty["SwapEffectUpgradeEnable"] = "1";
        Assert.Equal("SwapEffectUpgradeEnable=1;", GamingSettings.FormatDirectX(empty));
    }

    [Fact]
    public void Aviso_de_reiniciar_some_ao_desfazer_e_ao_reiniciar()
    {
        var path = _dir.File("restart.json");
        var boot = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);
        var tracker = new RestartTracker(path, boot);

        tracker.Changed("pagefile", "paginação", "C:0 0", "none");
        tracker.Changed("hags", "agendamento de GPU", "True", "False");
        Assert.Equal(["paginação", "agendamento de GPU"], tracker.PendingLabels);

        tracker.Changed("pagefile", "paginação", "none", "C:0 0"); // voltou ao valor deste boot
        Assert.Equal(["agendamento de GPU"], tracker.PendingLabels);

        Assert.Equal(["agendamento de GPU"], new RestartTracker(path, boot.AddSeconds(30)).PendingLabels); // mesmo boot (Folga reaberto)
        Assert.Empty(new RestartTracker(path, boot.AddHours(3)).PendingLabels);                          // reiniciou
    }

    private static (BoostEngine Engine, FakeProcessControl Control, FakeWatcher Watcher) Engine(FakePowerPlan power, Guid? scheme, ChangeJournal journal)
    {
        var control = new FakeProcessControl();
        var watcher = new FakeWatcher();
        var config = new FolgaConfig
        {
            Games = [new GameProfile { Name = "Jogo", Executables = ["jogo.exe"] }],
            Power = new PowerSettings { SchemeDuringGame = scheme },
        };
        return (new BoostEngine(config, TopologyTests.Ryzen5500X3D(), control, new FakeIfeo(), new FakeMemory(), journal, watcher, new FakeNetwork(), power: power), control, watcher);
    }

    [Fact]
    public void Plano_de_energia_troca_no_jogo_e_volta_ao_fechar()
    {
        var power = new FakePowerPlan();
        var journal = new ChangeJournal(_dir.File("journal.json"));
        var (engine, control, watcher) = Engine(power, FakePowerPlan.High, journal);
        using (engine)
        {
            engine.Start();
            control.Add(10, "jogo.exe");
            watcher.RaiseStarted(10, "jogo.exe");
            Assert.Equal(FakePowerPlan.High, power.Current);
            Assert.Contains(journal.Entries, e => e.Kind == ChangeKind.PowerScheme && e.ExeName == FakePowerPlan.Balanced.ToString());

            control.Kill(10);
            watcher.RaiseStopped(10);
            Assert.Equal(FakePowerPlan.Balanced, power.Current);
            Assert.DoesNotContain(journal.Entries, e => e.Kind == ChangeKind.PowerScheme);
        }
    }

    [Fact]
    public void Plano_de_energia_volta_depois_de_crash_e_nao_troca_sem_escolha()
    {
        var power = new FakePowerPlan { Current = FakePowerPlan.High };
        var journal = new ChangeJournal(_dir.File("journal.json"));
        journal.Add(new JournalEntry { Kind = ChangeKind.PowerScheme, ExeName = FakePowerPlan.Balanced.ToString() }); // o app caiu no meio do jogo

        var (engine, control, watcher) = Engine(power, null, journal);
        using (engine)
        {
            engine.Start();
            Assert.Equal(FakePowerPlan.Balanced, power.Current); // restaurado ao abrir

            control.Add(10, "jogo.exe");
            watcher.RaiseStarted(10, "jogo.exe");
            Assert.Equal(FakePowerPlan.Balanced, power.Current); // "não trocar"
            Assert.DoesNotContain(journal.Entries, e => e.Kind == ChangeKind.PowerScheme);
        }
    }

    [Fact]
    public void Plano_inexistente_ou_falha_ao_trocar_nao_deixa_lixo_no_diario()
    {
        var power = new FakePowerPlan { FailSet = true };
        var journal = new ChangeJournal(_dir.File("journal.json"));
        var (engine, control, watcher) = Engine(power, FakePowerPlan.High, journal);
        using (engine)
        {
            engine.Start();
            control.Add(10, "jogo.exe");
            watcher.RaiseStarted(10, "jogo.exe");
            Assert.Equal(FakePowerPlan.Balanced, power.Current);
            Assert.DoesNotContain(journal.Entries, e => e.Kind == ChangeKind.PowerScheme);
        }

        var (engine2, control2, watcher2) = Engine(new FakePowerPlan(), Guid.NewGuid(), journal);
        using (engine2)
        {
            engine2.Start();
            control2.Add(11, "jogo.exe");
            watcher2.RaiseStarted(11, "jogo.exe");
            Assert.DoesNotContain(journal.Entries, e => e.Kind == ChangeKind.PowerScheme);
        }
    }
}
