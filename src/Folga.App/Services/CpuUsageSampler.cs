using System.Runtime.InteropServices;
using Folga.Core.Cpu;
using Folga.Core.Logging;

namespace Folga.App.Services;

/// <summary>
/// Uso por thread lógica via PDH nativo ("Processor Information(*)\% Processor Utility", o mesmo do
/// Gerenciador de Tarefas). O PerformanceCounter gerenciado custava ~25 MB de RAM; a consulta direta, quase nada.
/// </summary>
public sealed unsafe partial class CpuUsageSampler : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    private readonly object _gate = new();
    private readonly Dictionary<string, uint> _idByInstance;
    private nint _query, _counter;

    public CpuUsageSampler(CpuTopology topology)
    {
        _idByInstance = topology.Sets.ToDictionary(s => $"{s.Group},{s.LogicalIndex}", s => s.Id);
        if (PdhOpenQueryW(null, 0, out _query) != 0
            || PdhAddEnglishCounterW(_query, @"\Processor Information(*)\% Processor Utility", 0, out _counter) != 0
            || PdhCollectQueryData(_query) != 0)
        {
            Log.Warn("Contadores de CPU (PDH) indisponíveis");
            Dispose();
        }
    }

    /// <summary>Uso de 0 a 100 por id de CPU Set e o total desde a última chamada; null se o PDH não está disponível.</summary>
    public (IReadOnlyDictionary<uint, double> PerThread, double Total)? Sample()
    {
        lock (_gate)
        {
            if (_query == 0 || PdhCollectQueryData(_query) != 0) return null;

            uint size = 0, count;
            if (PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, null) != PDH_MORE_DATA)
                return null;
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                if (PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, p) != 0) return null;

                var perThread = new Dictionary<uint, double>();
                double total = 0;
                var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)p;
                for (var i = 0; i < count; i++)
                {
                    var name = new string(items[i].Name);
                    var value = Math.Clamp(items[i].Value.DoubleValue, 0, 100);
                    if (name == "_Total") total = value;
                    else if (_idByInstance.TryGetValue(name, out var id)) perThread[id] = value;
                }
                return (perThread, total);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != 0) PdhCloseQuery(_query);
            _query = 0;
            _counter = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double DoubleValue; // união; com PDH_FMT_DOUBLE é sempre o double
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public char* Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial int PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial int PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* items);

    [LibraryImport("pdh.dll")]
    private static partial int PdhCloseQuery(nint query);
}
