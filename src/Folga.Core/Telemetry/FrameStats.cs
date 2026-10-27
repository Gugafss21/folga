namespace Folga.Core.Telemetry;

/// <summary>Um quadro: quando foi apresentado (ms, relógio do ETW) e quanto tempo levou desde o anterior.</summary>
public readonly record struct FrameSample(double AtMs, double FrameTimeMs);

/// <summary>
/// Frametimes dos últimos 60 s de uma cadeia de apresentação (swap chain), a partir dos horários de Present.
/// Mesmo princípio do PresentMon: frametime = intervalo entre dois Presents seguidos.
/// </summary>
public sealed class FrameStats
{
    public const double HistoryMs = 60_000;
    private const double MaxGapMs = 5_000; // pausa, alt-tab ou tela de carregamento: não é um quadro

    private readonly object _gate = new();
    private readonly Queue<FrameSample> _frames = new();
    private readonly FrameTimeHistogram _session = new();
    private double? _lastPresent;

    public void AddPresent(double atMs)
    {
        lock (_gate)
        {
            if (_lastPresent is { } last && atMs > last && atMs - last < MaxGapMs)
            {
                _frames.Enqueue(new FrameSample(atMs, atMs - last));
                _session.Add(atMs - last);
            }
            _lastPresent = atMs;
            while (_frames.Count > 0 && _frames.Peek().AtMs < atMs - HistoryMs) _frames.Dequeue();
        }
    }

    public int Count
    {
        get { lock (_gate) return _frames.Count; }
    }

    /// <summary>Resumo de todos os quadros desde que a cadeia apareceu (a sessão inteira, não só 60 s).</summary>
    public FrameSummary Session()
    {
        lock (_gate) return _session.Summary();
    }

    /// <summary>Quadros na janela que termina no último Present.</summary>
    public IReadOnlyList<FrameSample> Recent(double windowMs = HistoryMs)
    {
        lock (_gate)
        {
            if (_lastPresent is not { } end) return [];
            return _frames.Where(f => f.AtMs > end - windowMs).ToList();
        }
    }

    /// <summary>FPS médio do último segundo.</summary>
    public double? Fps(double windowMs = 1_000) => AverageFps(Recent(windowMs));

    /// <summary>
    /// 1% low: o FPS equivalente ao percentil 99 dos frametimes da janela (30 s por padrão).
    /// Mostra as travadinhas que o FPS médio esconde.
    /// </summary>
    public double? OnePercentLow(double windowMs = 30_000) => PercentileLowFps(Recent(windowMs), 0.99);

    public static double? AverageFps(IReadOnlyList<FrameSample> frames)
    {
        if (frames.Count == 0) return null;
        var total = frames.Sum(f => f.FrameTimeMs);
        return total <= 0 ? null : 1000 * frames.Count / total;
    }

    public static double? PercentileLowFps(IReadOnlyList<FrameSample> frames, double percentile)
    {
        if (frames.Count < 20) return null; // pouco quadro: o número enganaria
        var sorted = frames.Select(f => f.FrameTimeMs).OrderBy(ms => ms).ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return 1000 / sorted[index];
    }
}
