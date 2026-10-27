using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Folga.App.Services;
using Folga.Core.Engine;
using Folga.Core.Logging;
using Folga.Core.Telemetry;

namespace Folga.App.ViewModels;

public sealed partial class ThreadBarViewModel : ObservableObject
{
    public required uint CpuSetId { get; init; }
    [ObservableProperty] private double _usage;
    [ObservableProperty] private string _text = "—";
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
            Threads = c.Threads.Select(t => new ThreadBarViewModel { CpuSetId = t.Id }).ToList(),
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
    }

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
            GameNote = state.Enabled
                ? $"Esperando um destes: {string.Join(", ", _services.Engine.Config.Games.Where(g => g.Enabled).Select(g => g.Name))}."
                : "Boost desligado: nada será alterado quando um jogo abrir.";
        }

        HeldText = state.SessionActive ? state.HeldProcesses.ToString(CultureInfo.CurrentCulture) : "—";
        CleanupText = state.SessionActive ? $"{state.TrimCount} · {state.PurgeCount}" : "—";

        // Papel de cada núcleo: o que as regras ativas mandam para o fundo; o resto é do jogo.
        var config = _services.Engine.Config;
        var background = config.Background.Where(r => r.Enabled)
            .SelectMany(r => _services.Engine.Topology.BackgroundIds(r.Cores)).ToHashSet();
        foreach (var core in Cores)
        {
            core.IsBackground = core.Threads.All(t => background.Contains(t.CpuSetId));
            core.IsGameActive = state.SessionActive && !core.IsBackground;
            core.Role = core.IsBackground ? "fundo" : "jogo";
        }
        var bgCores = Cores.Where(c => c.IsBackground).Select(c => c.Title.Replace("Núcleo ", "")).ToList();
        CoreLegend = bgCores.Count == 0
            ? "Nenhum núcleo reservado ao fundo."
            : $"Durante o jogo, navegador e apps de fundo ficam no núcleo {string.Join(" e ", bgCores)}; o jogo usa todos.";
    }

    private async void SampleAsync()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var (cpu, memory, telemetry) = await Task.Run(() => (_services.CpuUsage.Sample(), _services.Memory.Read(), _services.Telemetry.Snapshot()));

            if (cpu is { } c)
            {
                CpuText = $"{c.Total:0}%";
                foreach (var thread in Cores.SelectMany(core => core.Threads))
                {
                    if (!c.PerThread.TryGetValue(thread.CpuSetId, out var usage)) continue;
                    thread.Usage = usage;
                    thread.Text = $"{usage:0}";
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
        TelemetryNote = IsSessionActive ? string.Join(" · ", notes) : "Os números do jogo aparecem quando um jogo da lista estiver aberto.";
    }

    private static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0", CultureInfo.CurrentCulture);

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(action);
    }
}
