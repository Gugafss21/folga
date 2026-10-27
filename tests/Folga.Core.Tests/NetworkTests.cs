using Folga.Core.Engine;
using Folga.Core.Journal;
using Folga.Core.Network;
using Folga.Core.Processes;
using Folga.Core.Profiles;
using Microsoft.Extensions.Time.Testing;

namespace Folga.Core.Tests;

public sealed class NetworkTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static FolgaConfig Config() => new()
    {
        Games = [new GameProfile { Name = "Arc Raiders", Executables = ["PioneerGame*.exe"], MarkDscp = true }],
        Background =
        [
            new BackgroundRule { Name = "Chrome", Executables = ["chrome.exe"], UploadLimitKbps = 4000 },
            new BackgroundRule { Name = "Discord", Executables = ["Discord.exe"] },
            new BackgroundRule { Name = "Epic", Executables = ["Epic*.exe"], UploadLimitKbps = 500 },
            new BackgroundRule { Name = "OneDrive", Executables = ["OneDrive.exe"], UploadLimitKbps = 1000, Enabled = false },
            new BackgroundRule { Name = "Spotify", Executables = ["Spotify.exe"], SuspendDuringGame = true },
        ],
    };

    [Fact]
    public void Plano_limita_upload_por_exe_e_marca_o_jogo()
    {
        var config = Config();
        var plan = NetworkPlanner.Build(config, [(config.Games[0], "PioneerGame-Win64-Shipping.exe")]);

        var chrome = Assert.Single(plan.Policies, p => p.Exe == "chrome.exe");
        Assert.Equal(4_000_000, chrome.ThrottleBitsPerSecond);
        Assert.Null(chrome.Dscp);
        Assert.StartsWith(NetworkPlanner.PolicyPrefix, chrome.Name);

        var game = Assert.Single(plan.Policies, p => p.Exe == "PioneerGame-Win64-Shipping.exe");
        Assert.Equal(46, game.Dscp);

        // Sem limite, com * (QoS não aceita) e regra desligada ficam de fora.
        Assert.DoesNotContain(plan.Policies, p => p.Exe is "Discord.exe" or "Epic*.exe" or "OneDrive.exe");
        Assert.True(plan.PauseWindowsUpdate);
    }

    [Fact]
    public void Sem_DSCP_e_sem_pausa_o_plano_pode_ficar_vazio()
    {
        var config = new FolgaConfig { Network = new NetworkSettings { PauseWindowsUpdate = false } };
        Assert.True(NetworkPlanner.Build(config, []).IsEmpty);
    }

    [Fact]
    public void Sessao_aplica_rede_ao_abrir_o_jogo_e_desfaz_ao_fechar()
    {
        var control = new FakeProcessControl();
        var watcher = new FakeWatcher();
        var network = new FakeNetwork();
        var spotify = control.Add(100, "Spotify.exe");
        using var engine = new BoostEngine(Config(), TopologyTests.Ryzen5500X3D(), control, new FakeIfeo(), new FakeMemory(),
            new ChangeJournal(_dir.File("journal.json")), watcher, network, new FakeTimeProvider());
        engine.Start();
        var endedAtStart = network.Ended;

        control.Add(200, "PioneerGame.exe");
        watcher.RaiseStarted(200, "PioneerGame.exe");

        var plan = Assert.Single(network.Begun);
        Assert.Contains(plan.Policies, p => p.Exe == "PioneerGame.exe" && p.Dscp == 46);
        Assert.Equal([1L], spotify.State[ChangeKind.Suspended]); // congelado

        control.Kill(200);
        watcher.RaiseStopped(200);
        Assert.True(network.Ended > endedAtStart);
        Assert.Equal([0L], spotify.State[ChangeKind.Suspended]); // descongelado
    }

    [Fact]
    public void Entradas_de_rede_no_diario_nao_sao_tratadas_como_processo()
    {
        var control = new FakeProcessControl();
        var network = new FakeNetwork();
        var journal = new ChangeJournal(_dir.File("journal.json"));
        journal.Add(new JournalEntry { Kind = ChangeKind.QosPolicy, ExeName = "Folga-up-chrome.exe" });
        journal.Add(new JournalEntry { Kind = ChangeKind.ServiceStopped, ExeName = "DoSvc" });

        using var engine = new BoostEngine(Config(), TopologyTests.Ryzen5500X3D(), control, new FakeIfeo(), new FakeMemory(),
            journal, new FakeWatcher(), network, new FakeTimeProvider());
        engine.Start();

        // O motor não tenta "desfazer" a rede como processo; deixa para o INetworkControl.
        Assert.Equal(2, journal.Entries.Count);
        Assert.True(network.Ended > 0);
    }

    [Fact]
    public void Config_v1_ganha_limite_de_upload_sem_perder_o_que_o_usuario_mudou()
    {
        var path = _dir.File("config.json");
        File.WriteAllText(path, """
            { "version": 1,
              "background": [
                { "name": "Chrome", "executables": ["chrome.exe"], "memoryBudgetMb": 1024 },
                { "name": "OneDrive", "executables": ["OneDrive.exe"] },
                { "name": "Discord", "executables": ["Discord.exe"], "ecoQos": false } ] }
            """);
        var config = new ConfigStore(path).Load();

        Assert.Equal(FolgaConfig.CurrentVersion, config.Version);
        Assert.Equal(1024, config.Background[0].MemoryBudgetMb);
        Assert.Equal(4000, config.Background[0].UploadLimitKbps);
        Assert.Equal(1000, config.Background[1].UploadLimitKbps);
        Assert.Equal(0, config.Background[2].UploadLimitKbps);
        Assert.True(config.Network.PauseWindowsUpdate);
        Assert.Contains($"\"version\": {FolgaConfig.CurrentVersion}", File.ReadAllText(path));
    }

    [Fact]
    public void Taxas_somam_processos_do_mesmo_app()
    {
        var before = new Dictionary<int, NetworkCounters> { [1] = new(1000, 5000), [2] = new(0, 0) };
        var after = new Dictionary<int, NetworkCounters> { [1] = new(3000, 25000), [2] = new(1000, 5000), [3] = new(0, 0) };
        var names = new Dictionary<int, string> { [1] = "chrome.exe", [2] = "chrome.exe", [3] = "idle.exe" };

        var rates = NetworkRates.Compute(before, after, TimeSpan.FromSeconds(2), pid => names.GetValueOrDefault(pid));

        var chrome = Assert.Single(rates);
        Assert.Equal("chrome.exe", chrome.ExeName);
        Assert.Equal(2, chrome.Processes);
        Assert.Equal(12500, chrome.DownloadBytesPerSecond); // (20000 + 5000) / 2
        Assert.Equal(1500, chrome.UploadBytesPerSecond);    // (2000 + 1000) / 2
    }
}
