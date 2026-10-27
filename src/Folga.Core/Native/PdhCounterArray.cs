using System.Runtime.InteropServices;

namespace Folga.Core.Native;

/// <summary>
/// Um contador PDH com curinga (ex.: "\Processor Information(*)\% Processor Utility"), lido direto do
/// pdh.dll. O PerformanceCounter gerenciado custava ~25 MB de RAM; a consulta direta, quase nada.
/// </summary>
public sealed unsafe partial class PdhCounterArray : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    private readonly object _gate = new();
    private nint _query, _counter;

    public PdhCounterArray(string englishPath)
    {
        if (PdhOpenQueryW(null, 0, out _query) != 0
            || PdhAddEnglishCounterW(_query, englishPath, 0, out _counter) != 0
            || PdhCollectQueryData(_query) != 0)
        {
            Dispose();
        }
    }

    public bool IsValid => _query != 0;

    /// <summary>Valores por instância desde a última leitura; null se o contador não está disponível.</summary>
    public IReadOnlyList<(string Instance, double Value)>? Read()
    {
        lock (_gate)
        {
            if (_query == 0 || PdhCollectQueryData(_query) != 0) return null;

            uint size = 0, count;
            if (PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, null) != PDH_MORE_DATA)
                return count == 0 ? [] : null;
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                if (PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, p) != 0) return null;
                var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)p;
                var result = new List<(string, double)>((int)count);
                for (var i = 0; i < count; i++) result.Add((new string(items[i].Name), items[i].Value.DoubleValue));
                return result;
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
