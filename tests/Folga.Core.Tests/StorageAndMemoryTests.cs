using Folga.Core.Journal;
using Folga.Core.Memory;
using Folga.Core.Processes;
using Folga.Core.Profiles;

namespace Folga.Core.Tests;

public sealed class StorageAndMemoryTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Diario_sobrevive_a_reabertura()
    {
        var path = _dir.File("journal.json");
        var journal = new ChangeJournal(path);
        var p = new ProcessRef(10, "chrome.exe", 123);
        journal.Add(new JournalEntry { Kind = ChangeKind.CpuSets, Pid = 10, ExeName = "chrome.exe", StartTime = 123, Original = [], Applied = [266, 267] });

        var reopened = new ChangeJournal(path);
        var entry = Assert.Single(reopened.Entries);
        Assert.True(reopened.Has(p, ChangeKind.CpuSets));
        Assert.Equal([266L, 267L], entry.Applied);

        reopened.RemoveWhere(_ => true);
        Assert.Empty(new ChangeJournal(path).Entries);
    }

    [Fact]
    public void Config_padrao_e_criada_e_relida()
    {
        var store = new ConfigStore(_dir.File("config.json"));
        var created = store.Load();
        Assert.True(File.Exists(store.FilePath));
        Assert.Contains(created.Background, r => r.Name == "Chrome" && r.MemoryBudgetMb == 2048);
        Assert.False(created.FindRule("Discord.exe")!.EcoQos);
        Assert.Null(created.FindRule("steam.exe"));

        created.Games[0].Executables.Add("Outro.exe");
        store.Save(created);
        var reloaded = store.Load();
        Assert.Contains("Outro.exe", reloaded.Games[0].Executables);
        Assert.Contains("\"High\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Config_invalida_nao_e_sobrescrita()
    {
        var path = _dir.File("config.json");
        File.WriteAllText(path, "{ quebrado");
        var config = new ConfigStore(path).Load();
        Assert.NotEmpty(config.Games);
        Assert.Equal("{ quebrado", File.ReadAllText(path));
        Assert.True(File.Exists(path + ".invalido"));
    }

    [Theory]
    [InlineData("PioneerGame*.exe", "PioneerGame-Win64-Shipping.exe", true)]
    [InlineData("chrome.exe", "CHROME.EXE", true)]
    [InlineData("chrome.exe", "chromedriver.exe", false)]
    [InlineData("", "chrome.exe", false)]
    public void Casamento_de_executavel(string pattern, string exe, bool expected) =>
        Assert.Equal(expected, ExeMatcher.Matches(pattern, exe));

    [Fact]
    public void Governador_escolhe_os_maiores_ate_caber()
    {
        var a = new ProcessRef(1, "chrome.exe", 1);
        var b = new ProcessRef(2, "chrome.exe", 2);
        var c = new ProcessRef(3, "chrome.exe", 3);
        var samples = new List<(ProcessRef, long)> { (a, 900), (b, 600), (c, 300) };

        Assert.Empty(MemoryGovernor.SelectTrimTargets(samples, 2000));
        Assert.Equal([a], MemoryGovernor.SelectTrimTargets(samples, 1000));
        Assert.Equal([a, b], MemoryGovernor.SelectTrimTargets(samples, 500));
        Assert.Empty(MemoryGovernor.SelectTrimTargets(samples, 0));
    }

    [Fact]
    public void Standby_respeita_limites_e_intervalo()
    {
        var settings = new MemorySettings();
        var now = DateTimeOffset.UtcNow;
        var low = new MemorySnapshot(16L << 30, 2L << 30, 512L << 20, 2L << 30);
        var plenty = low with { FreeBytes = 4L << 30 };
        var noCache = low with { StandbyBytes = 256L << 20 };

        Assert.True(StandbyPolicy.ShouldPurge(low, settings, now, null));
        Assert.False(StandbyPolicy.ShouldPurge(plenty, settings, now, null));
        Assert.False(StandbyPolicy.ShouldPurge(noCache, settings, now, null));
        Assert.False(StandbyPolicy.ShouldPurge(low, settings, now, now.AddSeconds(-30)));
        Assert.True(StandbyPolicy.ShouldPurge(low, settings, now, now.AddSeconds(-61)));
        Assert.False(StandbyPolicy.ShouldPurge(low, new MemorySettings { PurgeStandbyList = false }, now, null));
    }
}
