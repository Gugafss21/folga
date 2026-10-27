using Folga.Core.Cpu;
using Folga.Core.Engine;
using Folga.Core.Journal;
using Folga.Core.Memory;
using Folga.Core.Processes;
using Folga.Core.Profiles;
using Microsoft.Extensions.Time.Testing;

namespace Folga.Core.Tests;

public sealed class BoostEngineTests : IDisposable
{
    private const long High = 0x80, Normal = 0x20, BelowNormal = 0x4000;

    private readonly TempDir _dir = new();
    private readonly FakeProcessControl _control = new();
    private readonly FakeIfeo _ifeo = new();
    private readonly FakeMemory _memory = new();
    private readonly FakeWatcher _watcher = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => _dir.Dispose();

    private static FolgaConfig Config() => new()
    {
        Games = [new GameProfile { Name = "Arc Raiders", Executables = ["PioneerGame*.exe"] }],
        Background =
        [
            new BackgroundRule { Name = "Chrome", Executables = ["chrome.exe"], MemoryBudgetMb = 300 },
            new BackgroundRule { Name = "Discord", Executables = ["Discord.exe"], EcoQos = false },
        ],
    };

    private BoostEngine Engine(FolgaConfig? config = null, ChangeJournal? journal = null) =>
        new(config ?? Config(), TopologyTests.Ryzen5500X3D(), _control, _ifeo, _memory,
            journal ?? new ChangeJournal(_dir.File("journal.json")), _watcher, _time);

    [Fact]
    public void Jogo_abrindo_segura_o_fundo_e_fechar_desfaz_tudo()
    {
        var chrome = _control.Add(100, "chrome.exe");
        var discord = _control.Add(101, "Discord.exe");
        var notepad = _control.Add(102, "notepad.exe");
        using var engine = Engine();
        engine.Start();

        Assert.False(engine.GetState().SessionActive);
        Assert.Equal(Normal, chrome.State[ChangeKind.Priority][0]);

        var game = _control.Add(200, "PioneerGame-Win64-Shipping.exe");
        _watcher.RaiseStarted(200, game.Exe);

        Assert.True(engine.GetState().SessionActive);
        Assert.Equal(High, game.State[ChangeKind.Priority][0]);
        Assert.Equal(BelowNormal, chrome.State[ChangeKind.Priority][0]);
        Assert.Equal([1L, 1L], chrome.State[ChangeKind.PowerThrottling]);
        Assert.Equal([10L + 256, 11L + 256], chrome.State[ChangeKind.CpuSets]);
        Assert.Equal([0L, 0L], discord.State[ChangeKind.PowerThrottling]); // EcoQoS desligado na regra
        Assert.Equal(Normal, notepad.State[ChangeKind.Priority][0]);       // sem regra

        _control.Kill(200);
        _watcher.RaiseStopped(200);

        Assert.False(engine.GetState().SessionActive);
        Assert.Equal(Normal, chrome.State[ChangeKind.Priority][0]);
        Assert.Equal([0L, 0L], chrome.State[ChangeKind.PowerThrottling]);
        Assert.Empty(chrome.State[ChangeKind.CpuSets]);
        Assert.Equal(5L, chrome.State[ChangeKind.MemoryPriority][0]);
        Assert.Equal(Normal, discord.State[ChangeKind.Priority][0]);
    }

    [Fact]
    public void Processo_de_fundo_que_abre_durante_o_jogo_recebe_a_regra()
    {
        using var engine = Engine();
        engine.Start();
        _control.Add(200, "PioneerGame.exe");
        _watcher.RaiseStarted(200, "PioneerGame.exe");

        var tab = _control.Add(300, "chrome.exe");
        _watcher.RaiseStarted(300, "chrome.exe");

        Assert.Equal(BelowNormal, tab.State[ChangeKind.Priority][0]);
        Assert.Equal(1, engine.GetState().HeldProcesses);
    }

    [Fact]
    public void Jogo_ja_aberto_quando_o_app_inicia_e_detectado()
    {
        var game = _control.Add(200, "PioneerGame.exe");
        var chrome = _control.Add(100, "chrome.exe");
        using var engine = Engine();
        engine.Start();

        Assert.True(engine.GetState().SessionActive);
        Assert.Equal(High, game.State[ChangeKind.Priority][0]);
        Assert.Equal(BelowNormal, chrome.State[ChangeKind.Priority][0]);
    }

    [Fact]
    public void Acesso_negado_pelo_anti_cheat_cai_no_IFEO_e_restaurar_tudo_remove()
    {
        using var engine = Engine();
        engine.Start();
        var game = _control.Add(200, "PioneerGame.exe");
        game.DenyAccess = true;
        _watcher.RaiseStarted(200, "PioneerGame.exe");

        Assert.Equal(3, _ifeo.Get("PioneerGame.exe"));
        Assert.Contains("IFEO", engine.GetState().Games.Single().Note);

        // Fechar o jogo não remove o IFEO: ele serve para a próxima abertura.
        _control.Kill(200);
        _watcher.RaiseStopped(200);
        Assert.Equal(3, _ifeo.Get("PioneerGame.exe"));

        engine.RestoreAll();
        Assert.Null(_ifeo.Get("PioneerGame.exe"));
        Assert.False(engine.GetState().Enabled);
    }

    [Fact]
    public void IFEO_que_ja_existia_volta_ao_valor_original()
    {
        _ifeo.Values["PioneerGame.exe"] = 2;
        using var engine = Engine();
        engine.Start();
        _control.Add(200, "PioneerGame.exe").DenyAccess = true;
        _watcher.RaiseStarted(200, "PioneerGame.exe");
        Assert.Equal(3, _ifeo.Get("PioneerGame.exe"));

        engine.RestoreAll();
        Assert.Equal(2, _ifeo.Get("PioneerGame.exe"));
    }

    [Fact]
    public void Crash_no_meio_do_jogo_e_restaurado_ao_reabrir()
    {
        var chrome = _control.Add(100, "chrome.exe");
        _control.Add(200, "PioneerGame.exe");
        var journalPath = _dir.File("journal.json");

        var crashed = Engine(journal: new ChangeJournal(journalPath));
        crashed.Start();
        Assert.Equal(BelowNormal, chrome.State[ChangeKind.Priority][0]);
        // Simula o app morto: sem Dispose, o diário fica no disco. O jogo fecha nesse meio tempo.
        _control.Kill(200);

        var reopened = new ChangeJournal(journalPath);
        Assert.NotEmpty(reopened.Entries);
        using var engine = Engine(journal: reopened);
        engine.Start();

        Assert.Equal(Normal, chrome.State[ChangeKind.Priority][0]);
        Assert.Empty(reopened.Entries);
    }

    [Fact]
    public void PID_reaproveitado_nao_recebe_valor_de_outro_processo()
    {
        var chrome = _control.Add(100, "chrome.exe");
        _control.Add(200, "PioneerGame.exe");
        using var engine = Engine();
        engine.Start();

        // chrome 100 morre sem evento e outro processo nasce com o mesmo PID.
        _control.Kill(100);
        var impostor = _control.Add(100, "chrome.exe");
        impostor.State[ChangeKind.Priority] = [0x8000];

        _control.Kill(200);
        _watcher.RaiseStopped(200);

        Assert.Equal(0x8000L, impostor.State[ChangeKind.Priority][0]);
        Assert.NotEqual(chrome.Start, impostor.Start);
    }

    [Fact]
    public void Desligar_o_boost_desfaz_e_religar_reaplica()
    {
        var chrome = _control.Add(100, "chrome.exe");
        _control.Add(200, "PioneerGame.exe");
        using var engine = Engine();
        engine.Start();

        engine.SetEnabled(false);
        Assert.Equal(Normal, chrome.State[ChangeKind.Priority][0]);

        engine.SetEnabled(true);
        Assert.Equal(BelowNormal, chrome.State[ChangeKind.Priority][0]);
    }

    [Fact]
    public void Governador_esvazia_os_maiores_acima_do_orcamento_com_intervalo()
    {
        var big = _control.Add(100, "chrome.exe");
        big.WorkingSet = 250L << 20;
        var mid = _control.Add(101, "chrome.exe");
        mid.WorkingSet = 150L << 20;
        var small = _control.Add(102, "chrome.exe");
        small.WorkingSet = 50L << 20;
        _control.Add(200, "PioneerGame.exe");
        using var engine = Engine();
        engine.Start();

        engine.Tick(); // 450 MB > 300 MB → só o maior sai
        Assert.Equal(1, big.Trims);
        Assert.Equal(0, mid.Trims);

        big.WorkingSet = 400L << 20;
        engine.Tick(); // o maior está no intervalo de espera
        Assert.Equal(1, big.Trims);

        _time.Advance(TimeSpan.FromSeconds(61));
        engine.Tick();
        Assert.Equal(2, big.Trims);
    }

    [Fact]
    public void Standby_so_e_limpa_com_jogo_aberto_e_ram_livre_baixa()
    {
        _memory.Snapshot = new MemorySnapshot(16L << 30, 3L << 30, 500L << 20, 3L << 30);
        using var engine = Engine();
        engine.Start();
        engine.Tick();
        Assert.Equal(0, _memory.Purges);

        _control.Add(200, "PioneerGame.exe");
        _watcher.RaiseStarted(200, "PioneerGame.exe");
        engine.Tick();
        engine.Tick(); // dentro do intervalo mínimo
        Assert.Equal(1, _memory.Purges);
        Assert.Equal(1, engine.GetState().PurgeCount);
    }

    [Fact]
    public void Teto_de_working_set_e_aplicado_e_desfeito()
    {
        var config = Config();
        config.Background[0].HardWorkingSetCapMb = 512;
        var chrome = _control.Add(100, "chrome.exe");
        var original = chrome.State[ChangeKind.WorkingSetLimit];
        _control.Add(200, "PioneerGame.exe");
        using var engine = Engine(config);
        engine.Start();

        Assert.Equal(512L << 20, chrome.State[ChangeKind.WorkingSetLimit][1]);
        Assert.Equal(0x4L | 0x2L, chrome.State[ChangeKind.WorkingSetLimit][2]);

        engine.SetEnabled(false);
        Assert.Equal(original, chrome.State[ChangeKind.WorkingSetLimit]);
    }
}
