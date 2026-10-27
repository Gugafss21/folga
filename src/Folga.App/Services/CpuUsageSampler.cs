using Folga.Core.Cpu;
using Folga.Core.Logging;
using Folga.Core.Native;

namespace Folga.App.Services;

/// <summary>Uso por thread lógica pelo PDH ("% Processor Utility", o mesmo do Gerenciador de Tarefas).</summary>
public sealed class CpuUsageSampler : IDisposable
{
    private readonly Dictionary<string, uint> _idByInstance;
    private readonly PdhCounterArray _counter = new(@"\Processor Information(*)\% Processor Utility");

    public CpuUsageSampler(CpuTopology topology)
    {
        _idByInstance = topology.Sets.ToDictionary(s => $"{s.Group},{s.LogicalIndex}", s => s.Id);
        if (!_counter.IsValid) Log.Warn("Contadores de CPU (PDH) indisponíveis");
    }

    /// <summary>Uso de 0 a 100 por id de CPU Set e o total desde a última chamada; null se o PDH não está disponível.</summary>
    public (IReadOnlyDictionary<uint, double> PerThread, double Total)? Sample()
    {
        if (_counter.Read() is not { } values) return null;
        var perThread = new Dictionary<uint, double>();
        double total = 0;
        foreach (var (instance, value) in values)
        {
            var v = Math.Clamp(value, 0, 100);
            if (instance == "_Total") total = v;
            else if (_idByInstance.TryGetValue(instance, out var id)) perThread[id] = v;
        }
        return (perThread, total);
    }

    public void Dispose() => _counter.Dispose();
}
