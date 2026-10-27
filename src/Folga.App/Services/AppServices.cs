using Folga.Core.Cpu;
using Folga.Core.Detection;
using Folga.Core.Diagnostics;
using Folga.Core.Engine;
using Folga.Core.Journal;
using Folga.Core.Logging;
using Folga.Core.Memory;
using Folga.Core.Network;
using Folga.Core.Processes;
using Folga.Core.Profiles;
using Folga.Core.SystemSettings;
using Folga.Core.Telemetry;

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
    public required TelemetryService Telemetry { get; init; }
    public required AutoGameDetector Detector { get; init; }
    public required OverlayService Overlay { get; init; }
    public required IPowerPlan Power { get; init; }
    public required RestartTracker Restart { get; init; }

    public static AppServices Create(string? configPath = null)
    {
        var store = new ConfigStore(configPath ?? ConfigStore.DefaultPath);
        var control = new WindowsProcessControl();
        var memory = new WindowsSystemMemory();
        var topology = CpuTopology.FromSystem();
        var journal = new ChangeJournal(ChangeJournal.DefaultPath);
        var power = new WindowsPowerPlan();
        var engine = new BoostEngine(store.Load(), topology, control, new RegistryIfeoStore(), memory,
            journal, ProcessWatcherFactory.Create(control), new WindowsNetworkControl(journal), power: power);
        var telemetry = new TelemetryService(new GpuMonitor());
        // FPS e ping acompanham a sessão: ligam quando o jogo abre e desligam quando fecha.
        engine.StateChanged += () => telemetry.TrackGame(engine.GetState().Games.Select(g => g.Process.Pid).ToHashSet());

        var detector = new AutoGameDetector(new WindowsForegroundSource(), EtwPresentProbe.TryStart,
            () => engine.Config, () => engine.GetState().SessionActive);
        // O jogo detectado vira perfil na lista (lembrado nas próximas vezes) e o boost liga na hora.
        detector.Detected += game =>
        {
            Log.Info($"Jogo detectado automaticamente: {game.Name} ({game.ExeName}), tela cheia" + (game.Fps is { } fps ? $" a {fps:0} FPS" : ""));
            engine.AddGameProfile(new GameProfile { Name = game.Name, Executables = [game.ExeName], AutoDetected = true });
            store.Save(engine.Config);
        };

        return new AppServices
        {
            Store = store,
            Engine = engine,
            Control = control,
            Memory = memory,
            CpuUsage = new CpuUsageSampler(topology),
            Ram = SystemInfo.ReadRam(),
            Telemetry = telemetry,
            Detector = detector,
            Overlay = new OverlayService(engine, telemetry, memory),
            Power = power,
            Restart = new RestartTracker(RestartTracker.DefaultPath),
        };
    }

    /// <summary>Grava a configuração que está no motor (ex.: depois de ligar/desligar o boost).</summary>
    public void SaveEngineConfig() => Store.Save(Engine.Config);

    public void Dispose()
    {
        Detector.Dispose();
        Overlay.Dispose();
        Engine.Dispose();
        Telemetry.Dispose();
        CpuUsage.Dispose();
    }
}
