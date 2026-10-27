namespace Folga.Core.Telemetry;

/// <summary>Resumo dos quadros de uma partida. Hitches = travadas que dá para sentir (quadro acima de 50 ms).</summary>
public readonly record struct FrameSummary(long Frames, double TotalMs, double? AverageFps, double? OnePercentLow, double? PointOnePercentLow, long Hitches)
{
    public double HitchesPerMinute => TotalMs > 0 ? Hitches / (TotalMs / 60_000) : 0;
}

/// <summary>
/// Frametimes da partida inteira em faixas de 0,1 ms (até 250 ms; acima disso vai para a última). Ocupa
/// 10 KB fixos, seja a partida de 5 minutos ou de 5 horas, e dá os percentis sem guardar cada quadro.
/// </summary>
public sealed class FrameTimeHistogram
{
    public const double HitchMs = 50;
    private const double BinMs = 0.1;
    private const int Bins = 2500;

    private readonly int[] _bins = new int[Bins];
    private long _frames, _hitches;
    private double _totalMs;

    public void Add(double frameTimeMs)
    {
        if (frameTimeMs <= 0) return;
        _bins[Math.Min(Bins - 1, (int)(frameTimeMs / BinMs))]++;
        _frames++;
        _totalMs += frameTimeMs;
        if (frameTimeMs > HitchMs) _hitches++;
    }

    public FrameSummary Summary() => new(
        _frames, _totalMs,
        _totalMs > 0 ? 1000 * _frames / _totalMs : null,
        LowFps(0.99), LowFps(0.999), _hitches);

    /// <summary>FPS do percentil de frametime (0,99 = 1% low). Pouco quadro daria número enganoso.</summary>
    private double? LowFps(double percentile)
    {
        if (_frames < 100) return null;
        var target = (long)Math.Ceiling(percentile * _frames);
        long seen = 0;
        for (var i = 0; i < Bins; i++)
        {
            seen += _bins[i];
            if (seen >= target) return 1000 / ((i + 0.5) * BinMs); // meio da faixa
        }
        return null;
    }
}
