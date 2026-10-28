using System.Text.Json.Serialization;

namespace Folga.Core.Telemetry;

/// <summary>O que pode estar acontecendo no PC quando o jogo trava.</summary>
public enum HitchCause
{
    /// <summary>O jogo lendo muito do disco: carregando mapa, área nova ou texturas (streaming).</summary>
    GameDiskRead,
    /// <summary>O Windows buscando no disco páginas que já tinham saído da RAM (falta de RAM).</summary>
    HardFaults,
    /// <summary>Pouca RAM disponível no PC inteiro.</summary>
    LowRam,
    /// <summary>Memória da placa de vídeo cheia: texturas transbordam para a RAM.</summary>
    VramFull,
    /// <summary>Processador inteiro no limite.</summary>
    CpuFull,
    /// <summary>Outros programas usando processador junto com o jogo.</summary>
    OtherCpu,
    /// <summary>Primeiro minuto depois de o jogo voltar a desenhar (carregamento): compilação de shaders.</summary>
    AfterLoading,
}

/// <summary>Um segundo de jogo: o estado do PC, medido por fora do jogo. null = a fonte não informou.</summary>
public readonly record struct SystemSample(
    double? GameDiskMBps,
    double? HardFaultMBps,
    double? AvailableRamMb,
    double? VramPercent,
    double? CpuPercent,
    double? OtherCpuPercent,
    bool AfterLoading);

/// <summary>
/// Uma causa possível numa ou mais partidas: em quantos segundos ela estava presente, quantas travadas houve
/// nesses segundos e o valor médio da medida na hora das travadas e nos segundos sem travada. Guarda somas
/// (e não médias) para que partidas diferentes possam ser juntadas.
/// </summary>
public sealed class HitchFactor
{
    public HitchCause Cause { get; set; }
    /// <summary>Segundos em que a medida estava disponível.</summary>
    public long Seconds { get; set; }
    public long Hitches { get; set; }
    public long ConditionSeconds { get; set; }
    public long ConditionHitches { get; set; }
    /// <summary>Soma do valor, uma vez por travada (média = SumAtHitch / Hitches).</summary>
    public double SumAtHitch { get; set; }
    /// <summary>Soma do valor nos segundos sem travada (média = SumCalm / CalmSeconds).</summary>
    public double SumCalm { get; set; }
    public long CalmSeconds { get; set; }

    [JsonIgnore] public double? AverageAtHitch => Hitches > 0 ? SumAtHitch / Hitches : null;
    [JsonIgnore] public double? AverageCalm => CalmSeconds > 0 ? SumCalm / CalmSeconds : null;

    /// <summary>Parte das travadas que aconteceu com a causa presente (0 a 1).</summary>
    [JsonIgnore] public double ShareOfHitches => Hitches > 0 ? (double)ConditionHitches / Hitches : 0;

    /// <summary>Parte do tempo de jogo com a causa presente (0 a 1).</summary>
    [JsonIgnore] public double ShareOfTime => Seconds > 0 ? (double)ConditionSeconds / Seconds : 0;

    /// <summary>
    /// Quantas vezes mais trava com a causa presente do que sem ela. null quando um dos lados não tem tempo
    /// nenhum; infinito quando sem a causa não houve nenhuma travada.
    /// </summary>
    [JsonIgnore]
    public double? Ratio
    {
        get
        {
            var outSeconds = Seconds - ConditionSeconds;
            if (ConditionSeconds == 0 || outSeconds == 0) return null;
            var inside = (double)ConditionHitches / ConditionSeconds;
            var outside = (double)(Hitches - ConditionHitches) / outSeconds;
            return outside > 0 ? inside / outside : inside > 0 ? double.PositiveInfinity : null;
        }
    }

    public void Merge(HitchFactor other)
    {
        Seconds += other.Seconds;
        Hitches += other.Hitches;
        ConditionSeconds += other.ConditionSeconds;
        ConditionHitches += other.ConditionHitches;
        SumAtHitch += other.SumAtHitch;
        SumCalm += other.SumCalm;
        CalmSeconds += other.CalmSeconds;
    }
}

/// <summary>
/// Junta, segundo a segundo, as travadas da partida com o estado do PC naquele segundo. No fim, cada causa
/// diz se as travadas se concentram nos momentos em que ela está presente.
/// </summary>
public sealed class HitchProfiler
{
    public const double DiskThresholdMBps = 20;
    public const double HardFaultThresholdMBps = 8;
    public const double VramThresholdPercent = 95;
    public const double CpuThresholdPercent = 90;
    public const double OtherCpuThresholdPercent = 15;

    private readonly double _lowRamMb;
    private readonly Dictionary<HitchCause, HitchFactor> _factors = Enum.GetValues<HitchCause>().ToDictionary(c => c, c => new HitchFactor { Cause = c });

    /// <param name="totalRamMb">RAM do PC: pouca RAM = menos de 10% dela (no mínimo 1 GB).</param>
    public HitchProfiler(double totalRamMb) => _lowRamMb = Math.Max(1024, totalRamMb * 0.10);

    public double LowRamThresholdMb => _lowRamMb;

    public long Seconds { get; private set; }

    /// <summary>Um segundo de jogo e quantas travadas terminaram nele.</summary>
    public void Add(SystemSample s, long hitches)
    {
        Seconds++;
        Count(HitchCause.GameDiskRead, s.GameDiskMBps, v => v >= DiskThresholdMBps, hitches);
        Count(HitchCause.HardFaults, s.HardFaultMBps, v => v >= HardFaultThresholdMBps, hitches);
        Count(HitchCause.LowRam, s.AvailableRamMb, v => v < _lowRamMb, hitches);
        Count(HitchCause.VramFull, s.VramPercent, v => v >= VramThresholdPercent, hitches);
        Count(HitchCause.CpuFull, s.CpuPercent, v => v >= CpuThresholdPercent, hitches);
        Count(HitchCause.OtherCpu, s.OtherCpuPercent, v => v >= OtherCpuThresholdPercent, hitches);
        Count(HitchCause.AfterLoading, s.AfterLoading ? 1 : 0, v => v > 0, hitches);
    }

    private void Count(HitchCause cause, double? value, Func<double, bool> present, long hitches)
    {
        if (value is not { } v) return;
        var f = _factors[cause];
        f.Seconds++;
        f.Hitches += hitches;
        if (present(v))
        {
            f.ConditionSeconds++;
            f.ConditionHitches += hitches;
        }
        if (hitches > 0) f.SumAtHitch += v * hitches;
        else
        {
            f.SumCalm += v;
            f.CalmSeconds++;
        }
    }

    /// <summary>As causas com alguma medida (as que a fonte nunca informou ficam de fora).</summary>
    public List<HitchFactor> Factors() => _factors.Values.Where(f => f.Seconds > 0).ToList();
}

/// <summary>
/// O lado do tempo do diagnóstico: conta as travadas de cada segundo a partir do total da partida e marca o
/// primeiro minuto depois que o jogo volta a desenhar (início da partida e cada carregamento).
/// </summary>
public sealed class HitchRecorder(double totalRamMb)
{
    public static readonly TimeSpan AfterLoadingWindow = TimeSpan.FromMinutes(1);

    private readonly HitchProfiler _profiler = new(totalRamMb);
    private long _lastHitches;
    private bool _paused = true; // antes do primeiro quadro conta como carregando
    private DateTimeOffset? _resumedAt;

    /// <param name="drawing">false enquanto o jogo não apresenta quadros (pausa, minimizado, carregamento).</param>
    /// <param name="totalHitches">Travadas da partida até agora.</param>
    public void Tick(DateTimeOffset now, bool drawing, long totalHitches, SystemSample sample)
    {
        // O total caiu: outra cadeia de apresentação virou a principal (o jogo recriou a saída de vídeo). A conta
        // recomeça do total dela, em vez de esperar que ela passe o total da antiga.
        var hitches = Math.Max(0, totalHitches - _lastHitches);
        _lastHitches = totalHitches;
        if (!drawing)
        {
            _paused = true; // tempo parado não entra: não há quadro para travar
            return;
        }
        if (_paused)
        {
            _paused = false;
            _resumedAt = now;
        }
        _profiler.Add(sample with { AfterLoading = now - _resumedAt < AfterLoadingWindow }, hitches);
    }

    public long Seconds => _profiler.Seconds;

    public List<HitchFactor> Factors() => _profiler.Factors();
}

public static class HitchVerdict
{
    /// <summary>Com menos travadas que isso, qualquer concentração pode ser acaso.</summary>
    public const long MinHitches = 10;
    public const long MinConditionHitches = 5;
    public const double MinShare = 0.25;
    public const double MinRatio = 2;

    /// <summary>Soma as causas de várias partidas.</summary>
    public static List<HitchFactor> Combine(IEnumerable<IEnumerable<HitchFactor>?> sessions)
    {
        var total = new Dictionary<HitchCause, HitchFactor>();
        foreach (var factors in sessions)
        {
            if (factors is null) continue;
            foreach (var f in factors)
            {
                if (!total.TryGetValue(f.Cause, out var sum)) total[f.Cause] = sum = new HitchFactor { Cause = f.Cause };
                sum.Merge(f);
            }
        }
        return total.Values.ToList();
    }

    /// <summary>
    /// Causas prováveis, da mais forte para a mais fraca: presentes em boa parte das travadas e com o jogo
    /// travando bem mais nesses momentos do que no resto da partida.
    /// </summary>
    public static IReadOnlyList<HitchFactor> Likely(IEnumerable<HitchFactor> factors) =>
        factors.Where(IsLikely)
            .OrderByDescending(f => f.ConditionHitches * Math.Min(f.Ratio ?? 0, 20))
            .ToList();

    public static bool IsLikely(HitchFactor f) =>
        f.Hitches >= MinHitches && f.ConditionHitches >= MinConditionHitches && f.ShareOfHitches >= MinShare && f.Ratio >= MinRatio;
}
