using Folga.Core.Logging;

namespace Folga.Core.Telemetry;

public sealed record TelemetrySnapshot(
    double? Fps,
    double? OnePercentLow,
    double? FrameTimeMs,
    IReadOnlyList<FrameSample> FrameTimes,
    GpuSnapshot? Gpu,
    PingState? Ping,
    string? FpsNote);

/// <summary>Números da partida inteira, entregues quando o jogo fecha.</summary>
public sealed record SessionMetrics(DateTimeOffset Start, DateTimeOffset End, FrameSummary Frames, double? PingAverageMs, int? PingMaxMs, double? GpuAveragePercent,
    IReadOnlyList<HitchFactor>? HitchFactors = null);

/// <summary>
/// Junta FPS, GPU e ping. O monitor de FPS e o de ping só rodam com um jogo aberto; a GPU é lida sob demanda.
/// Durante a partida, ping, GPU e o estado do PC são amostrados a cada segundo para o resumo da sessão e o
/// diagnóstico das travadas.
/// </summary>
public sealed class TelemetryService : IDisposable
{
    private const long StaleAfterMs = 2_000; // sem Present há 2 s: jogo em pausa, minimizado ou carregando
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly object _gpuGate = new(); // a GPU é lida pela tela, pelo overlay e pela amostragem
    private readonly GpuMonitor _gpu;
    private readonly TimeProvider _time;
    private readonly Func<IReadOnlyCollection<int>, ISystemProbe?> _probeFactory;
    private EtwPresentMonitor? _present;
    private ISystemProbe? _probe;
    private HitchRecorder? _hitches;
    private PingMonitor? _ping;
    private string? _fpsNote;
    private ITimer? _sampler;
    private DateTimeOffset _start;
    private readonly List<int> _pings = [];
    private readonly List<double> _gpuUsage = [];

    /// <param name="probeFactory">Estado do PC para o diagnóstico das travadas; null desliga o diagnóstico.</param>
    public TelemetryService(GpuMonitor gpu, TimeProvider? time = null, Func<IReadOnlyCollection<int>, ISystemProbe?>? probeFactory = null)
    {
        _gpu = gpu;
        _time = time ?? TimeProvider.System;
        _probeFactory = probeFactory ?? (pids => new WindowsSystemProbe(pids));
    }

    /// <summary>Disparado fora da trava quando uma partida acaba (jogo fechou, boost desligado ou app saindo).</summary>
    public event Action<SessionMetrics>? SessionFinished;

    /// <summary>Passa a acompanhar estes PIDs de jogo; vazio desliga. Chamadas repetidas com os mesmos PIDs não fazem nada.</summary>
    public void TrackGame(IReadOnlyCollection<int> pids)
    {
        SessionMetrics? finished;
        lock (_gate)
        {
            if (_present is not null && _present.Pids.SetEquals(pids)) return;
            if (_present is null && pids.Count == 0) return;

            finished = StopLocked();
            if (pids.Count > 0) StartLocked(pids);
        }
        Raise(finished);
    }

    private void StartLocked(IReadOnlyCollection<int> pids)
    {
        _present = new EtwPresentMonitor(pids);
        try
        {
            _present.Start();
            _fpsNote = null;
            try { _probe = _probeFactory(pids); }
            catch (Exception ex) { Log.Warn($"Diagnóstico das travadas indisponível: {ex.Message}"); }
            _hitches = _probe is null ? null : new HitchRecorder(_probe.TotalRamMb);
        }
        catch (Exception ex)
        {
            _fpsNote = ex is UnauthorizedAccessException ? "FPS precisa do Folga como administrador" : $"FPS indisponível: {ex.Message}";
            Log.Warn($"Monitor de FPS: {ex.Message}");
        }
        _ping = new PingMonitor(pids);
        _start = _time.GetUtcNow();
        _pings.Clear();
        _gpuUsage.Clear();
        _sampler = _time.CreateTimer(_ => Sample(), null, SampleInterval, SampleInterval);
    }

    /// <summary>Uma amostra de ping, GPU e estado do PC para o resumo (o timer chama; os testes também).</summary>
    public void Sample()
    {
        var gpu = ReadGpu();
        lock (_gate)
        {
            if (_present is null) return;
            if (_ping?.State.RttMs is { } rtt) _pings.Add(rtt);
            if (gpu?.UsagePercent is { } usage) _gpuUsage.Add(usage);
            if (_probe is not null && _hitches is not null)
            {
                double? vram = gpu is { VramUsedBytes: { } used, VramTotalBytes: > 0 and { } total } ? 100.0 * used / total : null;
                _hitches.Tick(_time.GetUtcNow(), _present.MsSinceLastPresent <= StaleAfterMs, _present.Hitches, _probe.Read(vram));
            }
        }
    }

    public TelemetrySnapshot Snapshot()
    {
        var gpu = ReadGpu();
        lock (_gate)
        {
            if (_present is null) return new TelemetrySnapshot(null, null, null, [], gpu, null, null);

            var stats = _present.Main;
            var stale = _present.MsSinceLastPresent > StaleAfterMs;
            var fps = stale ? null : stats?.Fps();
            var note = _fpsNote ?? (stats is null || stats.Count == 0
                ? "esperando o jogo desenhar o primeiro quadro (DirectX 10, 11 ou 12)"
                : stale ? "jogo sem desenhar quadros (pausa, minimizado ou carregando)" : null);
            return new TelemetrySnapshot(
                fps,
                stats?.OnePercentLow(),
                fps is > 0 ? 1000 / fps : null,
                stats?.Recent() ?? [],
                gpu,
                _ping?.State,
                note);
        }
    }

    private GpuSnapshot? ReadGpu()
    {
        lock (_gpuGate) return _gpu.Read();
    }

    /// <summary>Para os monitores e devolve o resumo da partida que acabou.</summary>
    private SessionMetrics? StopLocked()
    {
        if (_present is null) return null;
        _sampler?.Dispose();
        _sampler = null;
        var metrics = new SessionMetrics(_start, _time.GetUtcNow(), _present.SessionSummary(),
            _pings.Count > 0 ? _pings.Average() : null, _pings.Count > 0 ? _pings.Max() : null,
            _gpuUsage.Count > 0 ? _gpuUsage.Average() : null,
            _hitches is { Seconds: > 0 } h ? h.Factors() : null);
        _present.Dispose();
        _present = null;
        _probe?.Dispose();
        _probe = null;
        _hitches = null;
        _ping?.Dispose();
        _ping = null;
        return metrics;
    }

    private void Raise(SessionMetrics? metrics)
    {
        if (metrics is null) return;
        try { SessionFinished?.Invoke(metrics); }
        catch (Exception ex) { Log.Error($"Erro guardando a sessão: {ex}"); }
    }

    public void Dispose()
    {
        SessionMetrics? finished;
        lock (_gate) finished = StopLocked();
        Raise(finished); // fechar o Folga no meio do jogo ainda guarda a partida
        _gpu.Dispose();
    }
}
