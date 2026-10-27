using Folga.Core.Cpu;
using Folga.Core.Detection;
using Folga.Core.Diagnostics;
using Folga.Core.Engine;
using Folga.Core.Journal;
using Folga.Core.Memory;
using Folga.Core.Processes;
using Folga.Core.Profiles;

namespace Folga.App.Services;

/// <summary>Monta o motor com as implementações reais do Windows.</summary>
public sealed class AppServices : IDisposable
{
    public required ConfigStore Store { get; init; }
    public required BoostEngine Engine { get; init; }
    public required WindowsProcessControl Control { get; init; }
    public required ISystemMemory Memory { get; init; }
    public required CpuUsageSampler CpuUsage { get; init; }
    public required RamInfo? Ram { get; init; }

    public static AppServices Create(string? configPath = null)
    {
        var store = new ConfigStore(configPath ?? ConfigStore.DefaultPath);
        var control = new WindowsProcessControl();
        var memory = new WindowsSystemMemory();
        var topology = CpuTopology.FromSystem();
        var engine = new BoostEngine(store.Load(), topology, control, new RegistryIfeoStore(), memory,
            new ChangeJournal(ChangeJournal.DefaultPath), ProcessWatcherFactory.Create(control));
        return new AppServices
        {
            Store = store,
            Engine = engine,
            Control = control,
            Memory = memory,
            CpuUsage = new CpuUsageSampler(topology),
            Ram = SystemInfo.ReadRam(),
        };
    }

    /// <summary>Grava a configuração que está no motor (ex.: depois de ligar/desligar o boost).</summary>
    public void SaveEngineConfig() => Store.Save(Engine.Config);

    public void Dispose()
    {
        Engine.Dispose();
        CpuUsage.Dispose();
    }
}
