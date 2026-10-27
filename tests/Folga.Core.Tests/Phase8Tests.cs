using Folga.Core.Engine;
using Folga.Core.Journal;
using Folga.Core.Memory;
using Folga.Core.Network;
using Folga.Core.Processes;
using Folga.Core.Profiles;
using Folga.Core.SystemSettings;

namespace Folga.Core.Tests;

public sealed class GameProfileOverrideTests : IDisposable
{
    private const long Normal = 0x20, BelowNormal = 0x4000;
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static FolgaConfig Config(GameProfile game) => new()
    {
        Games = [game],
        Background =
        [
            new BackgroundRule { Name = "Chrome", Executables = ["chrome.exe"], UploadLimitKbps = 4000 },
            new BackgroundRule { Name = "Discord", Executables = ["Discord.exe"], UploadLimitKbps = 1000 },
        ],
        Network = new NetworkSettings { PauseWindowsUpdate = true },
        Power = new PowerSettings { SchemeDuringGame = FakePowerPlan.High },
    };

    private (BoostEngine Engine, FakeProcessControl Control, FakeWatcher Watcher, FakeNetwork Network, FakePowerPlan Power) Engine(GameProfile game)
    {
        var control = new FakeProcessControl();
        var watcher = new FakeWatcher();
        var network = new FakeNetwork();
        var power = new FakePowerPlan();
        var engine = new BoostEngine(Config(game), TopologyTests.Ryzen5500X3D(), control, new FakeIfeo(), new FakeMemory(),
            new ChangeJournal(_dir.File($"journal-{Guid.NewGuid():N}.json")), watcher, network, power: power);
        return (engine, control, watcher, network, power);
    }

    [Fact]
    public void Jogo_que_nao_segura_o_fundo_so_ganha_prioridade()
    {
        var (engine, control, watcher, network, _) = Engine(new GameProfile { Name = "J", Executables = ["j.exe"], HoldBackground = false });
        using (engine)
        {
            var chrome = control.Add(100, "chrome.exe");
            engine.Start();
            var game = control.Add(10, "j.exe");
            watcher.RaiseStarted(10, "j.exe");

            Assert.Equal(0x80L, game.State[ChangeKind.Priority][0]);
            Assert.Equal(Normal, chrome.State[ChangeKind.Priority][0]);
            Assert.Equal(0, engine.GetState().HeldProcesses);
            Assert.Empty(Assert.Single(network.Begun).Policies); // sem limite de upload dos apps de fundo

            var discord = control.Add(101, "Discord.exe");
            watcher.RaiseStarted(101, "Discord.exe");             // app aberto no meio do jogo também fica em paz
            Assert.Equal(Normal, discord.State[ChangeKind.Priority][0]);
        }
    }

    [Fact]
    public void Regra_pulada_so_neste_jogo()
    {
        var (engine, control, watcher, network, _) = Engine(new GameProfile { Name = "J", Executables = ["j.exe"], SkipRules = ["discord"] });
        using (engine)
        {
            var chrome = control.Add(100, "chrome.exe");
            var discord = control.Add(101, "Discord.exe");
            engine.Start();
            control.Add(10, "j.exe");
            watcher.RaiseStarted(10, "j.exe");

            Assert.Equal(BelowNormal, chrome.State[ChangeKind.Priority][0]);
            Assert.Equal(Normal, discord.State[ChangeKind.Priority][0]);
            Assert.Equal(["chrome.exe"], Assert.Single(network.Begun).Policies.Select(p => p.Exe));
        }
    }

    [Fact]
    public void Plano_de_energia_e_Windows_Update_por_jogo()
    {
        var (engine, control, watcher, network, power) = Engine(new GameProfile { Name = "J", Executables = ["j.exe"], PowerScheme = "", PauseWindowsUpdate = false });
        using (engine)
        {
            engine.Start();
            control.Add(10, "j.exe");
            watcher.RaiseStarted(10, "j.exe");

            Assert.Equal(FakePowerPlan.Balanced, power.Current); // "" = não trocar, apesar do plano geral
            Assert.False(Assert.Single(network.Begun).PauseWindowsUpdate);
        }
    }

    [Fact]
    public void Padrao_segue_a_configuracao_geral_e_override_vale()
    {
        var config = Config(new GameProfile());
        var plain = new GameProfile();
        Assert.Equal(FakePowerPlan.High, config.PowerSchemeFor(plain));
        Assert.Equal(FakePowerPlan.Balanced, config.PowerSchemeFor(new GameProfile { PowerScheme = FakePowerPlan.Balanced.ToString() }));
        Assert.True(config.PauseWindowsUpdateFor(plain));
        Assert.Equal(config.Overlay.Enabled, config.OverlayFor(plain));
        Assert.False(config.OverlayFor(new GameProfile { Overlay = false }));

        var low = new MemorySnapshot(16L << 30, 1L << 30, 512L << 20, 6L << 30);
        var now = DateTimeOffset.UtcNow;
        Assert.True(StandbyPolicy.ShouldPurge(low, config.Memory, now, null));
        Assert.False(StandbyPolicy.ShouldPurge(low, config.Memory, now, null, enabled: config.PurgeStandbyFor(new GameProfile { PurgeStandby = false })));
    }

    [Fact]
    public void Ajustes_do_jogo_sobrevivem_ao_salvar()
    {
        var store = new ConfigStore(_dir.File("config.json"));
        var config = Config(new GameProfile { Name = "J", Executables = ["j.exe"], HoldBackground = false, SkipRules = ["Discord"], PowerScheme = "", Overlay = true });
        store.Save(config);
        var game = store.Load().Games[0];
        Assert.Equal((false, "Discord", "", true), (game.HoldBackground, game.SkipRules.Single(), game.PowerScheme, game.Overlay));
        Assert.Null(game.PurgeStandby);
    }
}

public sealed class UpdaterTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static string Release(string tag, bool draft = false, string? digest = "sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789", bool exe = true, bool checksumAsset = false) => $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "body": "Novidades",
          "assets": [
            {{(exe ? $$"""{ "name": "Folga.exe", "browser_download_url": "https://example/Folga.exe"{{(digest is null ? "" : $", \"digest\": \"{digest}\"")}} }""" : """{ "name": "outro.zip", "browser_download_url": "https://example/outro.zip" }""")}}
            {{(checksumAsset ? """, { "name": "Folga.exe.sha256", "browser_download_url": "https://example/Folga.exe.sha256" }""" : "")}}
          ]
        }
        """;

    [Fact]
    public void Release_mais_nova_com_digest()
    {
        var info = Updater.Parse(Release("v0.8.0"), new Version(0, 7, 0));
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 8, 0), info.Version);
        Assert.Equal("https://example/Folga.exe", info.DownloadUrl);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", info.Sha256);
        Assert.Equal("Novidades", info.Notes);
    }

    [Theory]
    [InlineData("v0.7.0")]  // mesma versão
    [InlineData("v0.6.9")]  // mais velha
    [InlineData("beta")]    // tag sem versão
    public void Sem_versao_nova(string tag) => Assert.Null(Updater.Parse(Release(tag), new Version(0, 7, 0)));

    [Fact]
    public void Rascunho_ou_sem_o_exe_nao_conta_e_sem_digest_usa_o_arquivo_sha256()
    {
        Assert.Null(Updater.Parse(Release("v1.0.0", draft: true), new Version(0, 7, 0)));
        Assert.Null(Updater.Parse(Release("v1.0.0", exe: false), new Version(0, 7, 0)));

        var info = Updater.Parse(Release("v1.0.0", digest: null, checksumAsset: true), new Version(0, 7, 0))!;
        Assert.Null(info.Sha256);
        Assert.Equal("https://example/Folga.exe.sha256", info.ChecksumUrl);
        Assert.Equal(new string('a', 64), Updater.ParseChecksum(new string('A', 64) + "  Folga.exe\n"));
        Assert.Null(Updater.ParseChecksum("não é hash"));
    }

    [Fact]
    public void Troca_o_exe_so_com_o_hash_certo()
    {
        var exe = _dir.File("Folga.exe");
        File.WriteAllText(exe, "versão velha");
        var downloaded = _dir.File("Folga.new.exe");

        File.WriteAllText(downloaded, "versão adulterada");
        Assert.Throws<InvalidDataException>(() => Updater.Apply(downloaded, new string('0', 64), exe));
        Assert.Equal("versão velha", File.ReadAllText(exe)); // nada trocado
        Assert.False(File.Exists(downloaded));               // o arquivo ruim some

        File.WriteAllText(downloaded, "versão nova");
        Updater.Apply(downloaded, Updater.Sha256Of(downloaded).ToUpperInvariant(), exe);
        Assert.Equal("versão nova", File.ReadAllText(exe));
        Assert.Equal("versão velha", File.ReadAllText(_dir.File("Folga.old.exe")));

        AppInstaller.CleanUpOldExe(exe); // próxima abertura
        Assert.False(File.Exists(_dir.File("Folga.old.exe")));
    }
}
