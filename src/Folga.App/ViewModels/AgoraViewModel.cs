using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Folga.App.Services;
using Folga.Core.Engine;
using Folga.Core.Logging;
using Folga.Core.Processes;
using Folga.Core.Profiles;
using Folga.Core.Telemetry;

namespace Folga.App.ViewModels;

public sealed partial class ThreadBarViewModel : ObservableObject
{
    public required uint CpuSetId { get; init; }
    /// <summary>Número do processador lógico no Windows (para casar com a amostragem do kernel); null fora do grupo 0.</summary>
    public required int? Processor { get; init; }
    [ObservableProperty] private double _usage;
    /// <summary>Parte do uso que é do jogo, medida pela amostragem do kernel (0 sem jogo ou sem a medida).</summary>
    [ObservableProperty] private double _gameUsage;
    [ObservableProperty] private string _text = "—";
    [ObservableProperty] private string _hint = "Uso desta thread, em %.";
}

public sealed partial class CoreTileViewModel : ObservableObject
{
    public required string Title { get; init; }
    public required IReadOnlyList<ThreadBarViewModel> Threads { get; init; }
    [ObservableProperty] private bool _isBackground;
    [ObservableProperty] private bool _isGameActive;
    [ObservableProperty] private string _role = "";
}

public sealed partial class AgoraViewModel : ObservableObject
{
    private const int ActivityLimit = 8;
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private int _sampling;

    public AgoraViewModel(AppServices services)
    {
        _services = services;
        Cores = new ObservableCollection<CoreTileViewModel>(services.Engine.Topology.Cores.Select(c => new CoreTileViewModel
        {
            Title = $"Núcleo {c.Index + 1}",
            Threads = c.Threads.Select(t => new ThreadBarViewModel { CpuSetId = t.Id, Processor = t.Group == 0 ? t.LogicalIndex : null }).ToList(),
        }));
        CoreColumns = Math.Min(Cores.Count, 8);
        RamWarning = services.Ram?.Warning;
        RamSpeed = services.Ram is { ConfiguredMhz: > 0 } ram ? $"RAM a {ram.ConfiguredMhz} MHz" : null;

        foreach (var line in Log.RecentLines().TakeLast(ActivityLimit)) Activity.Add(line);
        Log.LineWritten += line => Dispatch(() =>
        {
            Activity.Add(line);
            while (Activity.Count > ActivityLimit) Activity.RemoveAt(0);
        });
        services.Engine.StateChanged += () => Dispatch(RefreshEngine);

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SampleAsync(), Dispatcher.CurrentDispatcher);
        RefreshEngine();
    }

    /// <summary>Só amostra CPU/RAM com a tela à vista: escondido na bandeja durante o jogo, o Folga não gasta nada com isso.</summary>
    [ObservableProperty] private bool _isVisible;

    partial void OnIsVisibleChanged(bool value)
    {
        if (value) { SampleAsync(); _timer.Start(); }
        else _timer.Stop();
        UpdateCoreMonitor();
    }

    // ---- uso real dos núcleos pelo jogo ----

    private EtwCoreUsageMonitor? _coreMonitor;
    private HashSet<int> _coreMonitorPids = [];
    private int _coreMonitorVersion;

    /// <summary>Liga a amostragem do kernel só com a tela à vista e um jogo aberto; troca quando os PIDs mudam.</summary>
    private void UpdateCoreMonitor()
    {
        var state = _services.Engine.GetState();
        var pids = IsVisible && state.SessionActive ? state.Games.Select(g => g.Process.Pid).ToHashSet() : [];
        if (pids.SetEquals(_coreMonitorPids) && (_coreMonitor is not null || pids.Count == 0)) return;

        var old = _coreMonitor;
        _coreMonitor = null;
        _coreMonitorPids = pids;
        var version = ++_coreMonitorVersion;
        Task.Run(() =>
        {
            old?.Dispose();
            if (pids.Count == 0) return;
            var monitor = new EtwCoreUsageMonitor(pids);
            try
            {
                monitor.Start();
            }
            catch (Exception ex)
            {
                monitor.Dispose();
                Dispatch(() => CoreSplitNote = ex is UnauthorizedAccessException
                    ? "O uso de cada núcleo pelo jogo precisa do Folga como administrador; as barras mostram o uso total."
                    : $"Uso dos núcleos pelo jogo indisponível ({ex.Message}); as barras mostram o uso total.");
                return;
            }
            Dispatch(() =>
            {
                if (version == _coreMonitorVersion) { _coreMonitor = monitor; CoreSplitNote = null; }
                else Task.Run(monitor.Dispose); // a tela fechou ou o jogo trocou enquanto ligava
            });
        });
    }

    [ObservableProperty] private string? _coreSplitNote;

    public ObservableCollection<CoreTileViewModel> Cores { get; }
    public int CoreColumns { get; }
    public ObservableCollection<string> Activity { get; } = [];
    public string? RamWarning { get; }
    public string? RamSpeed { get; }

    [ObservableProperty] private string _gameTitle = "Nenhum jogo aberto";
    [ObservableProperty] private string _gameNote = "";
    [ObservableProperty] private bool _isSessionActive;
    [ObservableProperty] private bool _boostEnabled;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _ramUsedText = "—";
    [ObservableProperty] private string _ramTotalText = "";
    [ObservableProperty] private string _freeText = "—";
    [ObservableProperty] private string _standbyText = "—";
    [ObservableProperty] private string _heldText = "—";
    [ObservableProperty] private string _cleanupText = "—";
    [ObservableProperty] private string _coreLegend = "";

    // Telemetria do jogo (Fase 4)
    [ObservableProperty] private string _fpsText = "—";
    [ObservableProperty] private string _lowText = "—";
    [ObservableProperty] private string _frameTimeText = "—";
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private string _vramText = "—";
    [ObservableProperty] private string _vramTotalText = "";
    [ObservableProperty] private string _gpuTempText = "—";
    [ObservableProperty] private string _pingText = "—";
    [ObservableProperty] private string _gpuName = "GPU";
    [ObservableProperty] private string _telemetryNote = "";
    [ObservableProperty] private IReadOnlyList<FrameSample> _frameTimes = [];

    private bool _syncingBoost;

    partial void OnBoostEnabledChanged(bool value)
    {
        if (_syncingBoost) return;
        _services.Engine.SetEnabled(value);
        _services.SaveEngineConfig();
    }

    private void RefreshEngine()
    {
        UpdateCoreMonitor();
        var state = _services.Engine.GetState();
        _syncingBoost = true;
        BoostEnabled = state.Enabled;
        _syncingBoost = false;
        IsSessionActive = state.SessionActive;

        if (state.Games.Count > 0)
        {
            var game = state.Games[0];
            GameTitle = game.ProfileName;
            var pids = string.Join(", ", state.Games.Select(g => g.Process.Pid));
            GameNote = $"{game.Process.ExeName} · PID {pids} · {game.Note}";
        }
        else
        {
            GameTitle = "Nenhum jogo aberto";
            var listed = string.Join(", ", _services.Engine.Config.Games.Where(g => g.Enabled).Select(g => g.Name));
            GameNote = !state.Enabled ? "Boost desligado: nada será alterado quando um jogo abrir."
                : _services.Engine.Config.AutoDetect.Enabled ? $"Esperando {(listed.Length > 0 ? listed + " ou " : "")}qualquer jogo em tela cheia."
                : $"Esperando um destes: {listed}.";
        }

        HeldText = state.SessionActive ? state.HeldProcesses.ToString(CultureInfo.CurrentCulture) : "—";
        CleanupText = state.SessionActive ? $"{state.TrimCount} · {state.PurgeCount}" : "—";

        // Papel de cada núcleo, lido do Windows: onde o jogo pode rodar (CPU Sets dele agora) e onde as regras
        // põem o fundo.
        var config = _services.Engine.Config;
        var topology = _services.Engine.Topology;
        var background = config.Background.Where(r => r.Enabled).SelectMany(r => topology.BackgroundIds(r.Cores)).ToHashSet();
        HashSet<uint>? gameAllowed = null; // null = todos
        if (state.SessionActive && state.Games.Count > 0
            && _services.Control.Read(state.Games[0].Process, ChangeKind.CpuSets, out var sets) == TweakResult.Ok && sets.Length > 0)
            gameAllowed = sets.Select(s => (uint)s).ToHashSet();

        foreach (var core in Cores)
        {
            var bg = core.Threads.All(t => background.Contains(t.CpuSetId));
            var game = state.SessionActive && (gameAllowed is null || core.Threads.Any(t => gameAllowed.Contains(t.CpuSetId)));
            core.IsBackground = bg;
            core.IsGameActive = game;
            core.Role = game && bg ? "jogo + fundo" : game ? "jogo" : bg ? "fundo" : state.SessionActive ? "sem jogo" : "";
        }
        CoreLegend = Legend(state.SessionActive, gameAllowed is null, background.Count > 0);
    }

    private string Legend(bool session, bool gameEverywhere, bool hasBackground)
    {
        string Numbers(Func<CoreTileViewModel, bool> pick)
        {
            var n = Cores.Where(pick).Select(c => c.Title.Replace("Núcleo ", "")).ToList();
            return n.Count <= 1 ? string.Concat(n) : $"{string.Join(", ", n[..^1])} e {n[^1]}";
        }
        var bg = hasBackground ? $"Navegador e apps de fundo ficam no núcleo {Numbers(c => c.IsBackground)}." : "Nenhum núcleo reservado ao fundo.";
        if (!session) return $"Sem jogo aberto. Durante o jogo: {bg[0].ToString().ToLowerInvariant()}{bg[1..]}";
        var game = gameEverywhere ? "O jogo pode rodar em todos os núcleos." : $"O jogo pode rodar só nos núcleos {Numbers(c => c.IsGameActive)} (escolhidos na tela Jogos).";
        return $"{game} {bg} Âmbar é o que o jogo está usando agora; cinza, os outros programas.";
    }
    private async void SampleAsync()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var monitor = _coreMonitor;
            var (cpu, memory, telemetry, split) = await Task.Run(() => (_services.CpuUsage.Sample(), _services.Memory.Read(), _services.Telemetry.Snapshot(), monitor?.Take()));

            if (cpu is { } c)
            {
                CpuText = $"{c.Total:0}%";
                foreach (var thread in Cores.SelectMany(core => core.Threads))
                {
                    if (!c.PerThread.TryGetValue(thread.CpuSetId, out var usage)) continue;
                    var share = split is not null && thread.Processor is { } p && p < split.Length ? split[p] : null;
                    thread.Usage = usage;
                    thread.GameUsage = share is { } s ? Math.Min(s.GamePercent, usage) : 0;
                    thread.Text = $"{usage:0}";
                    thread.Hint = share is { } sh
                        ? $"Uso desta thread: {usage:0}%. Do jogo: {sh.GamePercent:0}%; outros programas: {sh.OtherPercent:0}%."
                        : $"Uso desta thread: {usage:0}%.";
                }
            }

            RamUsedText = Gb(memory.UsedBytes);
            RamTotalText = $"de {Gb(memory.TotalBytes)} GB";
            FreeText = Gb(memory.FreeBytes);
            StandbyText = Gb(memory.StandbyBytes);
            ApplyTelemetry(telemetry);
        }
        catch (Exception ex)
        {
            Log.Error($"Falha ao amostrar CPU/RAM: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    private void ApplyTelemetry(TelemetrySnapshot t)
    {
        FpsText = t.Fps is { } fps ? fps.ToString("0", CultureInfo.CurrentCulture) : "—";
        LowText = t.OnePercentLow is { } low ? low.ToString("0", CultureInfo.CurrentCulture) : "—";
        FrameTimeText = t.FrameTimeMs is { } ms ? ms.ToString("0.0", CultureInfo.CurrentCulture) : "—";
        FrameTimes = t.FrameTimes;

        GpuName = t.Gpu?.Name ?? "GPU";
        GpuText = t.Gpu?.UsagePercent is { } usage ? $"{usage:0}%" : "—";
        VramText = t.Gpu?.VramUsedBytes is { } used ? Gb(used) : "—";
        VramTotalText = t.Gpu?.VramTotalBytes is { } total ? $"de {Gb(total)} GB" : "";
        GpuTempText = t.Gpu?.TemperatureC is { } c ? $"{c}°" : "—";

        PingText = t.Ping?.RttMs is { } rtt ? rtt.ToString(CultureInfo.CurrentCulture) : "—";
        var notes = new[] { t.FpsNote, t.Ping is { RttMs: null } p ? "Ping: " + p.Status : null }.Where(n => n is not null);
        TelemetryNote = IsSessionActive ? string.Join(" · ", notes) : "Os números do jogo aparecem quando um jogo estiver aberto.";
    }

    private static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0", CultureInfo.CurrentCulture);

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(action);
    }
}
