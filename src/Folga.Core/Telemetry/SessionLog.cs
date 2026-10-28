using System.Text.Json;
using Folga.Core.Logging;
using Folga.Core.Profiles;

namespace Folga.Core.Telemetry;

/// <summary>Uma partida medida. Boost = false: jogada no modo "só medir", para comparação.</summary>
public sealed class SessionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string Game { get; set; } = "";
    public bool Boost { get; set; }
    public long Frames { get; set; }
    public double FrameMs { get; set; }
    public double? AverageFps { get; set; }
    public double? OnePercentLow { get; set; }
    public double? PointOnePercentLow { get; set; }
    public long Hitches { get; set; }
    public double? PingAverageMs { get; set; }
    public int? PingMaxMs { get; set; }
    public double? GpuAveragePercent { get; set; }
    /// <summary>Diagnóstico das travadas (null em partidas medidas antes da v0.9 ou sem leitura do PC).</summary>
    public List<HitchFactor>? HitchFactors { get; set; }

    public double Minutes => (End - Start).TotalMinutes;
    public double HitchesPerMinute => FrameMs > 0 ? Hitches / (FrameMs / 60_000) : 0;
}

/// <summary>Média de várias partidas de um jogo num modo (com ou sem boost), ponderada pelo tempo de jogo.</summary>
public sealed record SessionAverage(int Sessions, double Minutes, double? AverageFps, double? OnePercentLow, double? PointOnePercentLow, double HitchesPerMinute, double? PingAverageMs);

public sealed record SessionComparison(string Game, SessionAverage? Boost, SessionAverage? Plain);

/// <summary>
/// Histórico de partidas (%LocalAppData%\Folga\sessions.json, as últimas 300). Partidas curtas ou sem
/// quadros medidos não entram: com pouco tempo, o número não diz nada.
/// </summary>
public sealed class SessionLog
{
    public const int Keep = 300;
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(2);
    public const long MinFrames = 1_000;

    private readonly object _gate = new();
    private readonly string _path;
    private List<SessionRecord> _records;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Folga", "sessions.json");

    public SessionLog(string path)
    {
        _path = path;
        _records = Load();
    }

    public event Action? Changed;

    public IReadOnlyList<SessionRecord> All
    {
        get { lock (_gate) return _records.ToList(); }
    }

    /// <summary>Guarda a partida; devolve null (e não guarda) se foi curta demais ou sem FPS medido.</summary>
    public SessionRecord? Add(string game, bool boost, SessionMetrics m)
    {
        if (m.End - m.Start < MinDuration || m.Frames.Frames < MinFrames) return null;
        var record = new SessionRecord
        {
            Start = m.Start, End = m.End, Game = game, Boost = boost,
            Frames = m.Frames.Frames, FrameMs = m.Frames.TotalMs,
            AverageFps = m.Frames.AverageFps, OnePercentLow = m.Frames.OnePercentLow, PointOnePercentLow = m.Frames.PointOnePercentLow,
            Hitches = m.Frames.Hitches,
            PingAverageMs = m.PingAverageMs, PingMaxMs = m.PingMaxMs, GpuAveragePercent = m.GpuAveragePercent,
            HitchFactors = m.HitchFactors?.ToList(),
        };
        lock (_gate)
        {
            _records.Add(record);
            if (_records.Count > Keep) _records.RemoveRange(0, _records.Count - Keep);
            Save();
        }
        Changed?.Invoke();
        return record;
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            if (_records.RemoveAll(r => r.Id == id) == 0) return;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Por jogo, a média com boost e a média sem boost (só medir), das partidas mais novas para as mais velhas.</summary>
    public static IReadOnlyList<SessionComparison> Compare(IEnumerable<SessionRecord> records) =>
        records.GroupBy(r => r.Game, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SessionComparison(g.Key, Average(g.Where(r => r.Boost)), Average(g.Where(r => !r.Boost))))
            .OrderByDescending(c => (c.Boost?.Sessions ?? 0) + (c.Plain?.Sessions ?? 0))
            .ToList();

    public static SessionAverage? Average(IEnumerable<SessionRecord> records)
    {
        var list = records.ToList();
        if (list.Count == 0) return null;
        var frameMs = list.Sum(r => r.FrameMs);
        return new SessionAverage(
            list.Count,
            list.Sum(r => r.Minutes),
            frameMs > 0 ? 1000 * list.Sum(r => r.Frames) / frameMs : null,
            Weighted(list, r => r.OnePercentLow),
            Weighted(list, r => r.PointOnePercentLow),
            frameMs > 0 ? list.Sum(r => r.Hitches) / (frameMs / 60_000) : 0,
            Weighted(list, r => r.PingAverageMs));
    }

    /// <summary>Média ponderada pelo tempo de quadros de cada partida, ignorando as que não têm o valor.</summary>
    private static double? Weighted(List<SessionRecord> list, Func<SessionRecord, double?> value)
    {
        var with = list.Where(r => value(r) is not null && r.FrameMs > 0).ToList();
        var weight = with.Sum(r => r.FrameMs);
        return weight > 0 ? with.Sum(r => value(r)!.Value * r.FrameMs) / weight : null;
    }

    private List<SessionRecord> Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<SessionRecord>>(File.ReadAllText(_path), ConfigStore.Json) ?? [] : [];
        }
        catch (JsonException ex)
        {
            Log.Error($"sessions.json ilegível ({ex.Message}); começando vazio");
            return [];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_records, ConfigStore.Json));
    }
}
