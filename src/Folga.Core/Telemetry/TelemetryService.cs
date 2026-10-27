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

/// <summary>
/// Junta FPS, GPU e ping. O monitor de FPS e o de ping só rodam com um jogo aberto; a GPU é lida sob demanda.
/// </summary>
public sealed class TelemetryService : IDisposable
{
    private const long StaleAfterMs = 2_000; // sem Present há 2 s: jogo em pausa, minimizado ou carregando

    private readonly object _gate = new();
    private readonly GpuMonitor _gpu;
    private EtwPresentMonitor? _present;
    private PingMonitor? _ping;
    private string? _fpsNote;

    public TelemetryService(GpuMonitor gpu) => _gpu = gpu;

    /// <summary>Passa a acompanhar estes PIDs de jogo; vazio desliga. Chamadas repetidas com os mesmos PIDs não fazem nada.</summary>
    public void TrackGame(IReadOnlyCollection<int> pids)
    {
        lock (_gate)
        {
            if (_present is not null && _present.Pids.SetEquals(pids)) return;
            if (_present is null && pids.Count == 0) return;

            StopLocked();
            if (pids.Count == 0) return;

            _present = new EtwPresentMonitor(pids);
            try
            {
                _present.Start();
                _fpsNote = null;
            }
            catch (Exception ex)
            {
                _fpsNote = ex is UnauthorizedAccessException ? "FPS precisa do Folga como administrador" : $"FPS indisponível: {ex.Message}";
                Log.Warn($"Monitor de FPS: {ex.Message}");
            }
            _ping = new PingMonitor(pids);
        }
    }

    public TelemetrySnapshot Snapshot()
    {
        var gpu = _gpu.Read();
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

    private void StopLocked()
    {
        _present?.Dispose();
        _present = null;
        _ping?.Dispose();
        _ping = null;
    }

    public void Dispose()
    {
        lock (_gate) StopLocked();
        _gpu.Dispose();
    }
}
