using System.Buffers.Binary;
using Folga.Core.Native;
using Folga.Core.Profiles;

namespace Folga.Core.Cpu;

public sealed record CpuSetInfo(uint Id, ushort Group, byte LogicalIndex, byte CoreIndex, byte LastLevelCacheIndex, byte EfficiencyClass);

public sealed record CpuCore(int Index, ushort Group, byte LastLevelCacheIndex, byte EfficiencyClass, IReadOnlyList<CpuSetInfo> Threads)
{
    public IEnumerable<uint> Ids => Threads.Select(t => t.Id);
}

/// <summary>Mapa dos núcleos da máquina, montado a partir dos CPU Sets do Windows.</summary>
public sealed class CpuTopology
{
    public IReadOnlyList<CpuSetInfo> Sets { get; }
    public IReadOnlyList<CpuCore> Cores { get; }

    /// <summary>CPU híbrida (Intel P/E): há mais de uma classe de eficiência.</summary>
    public bool IsHybrid { get; }

    /// <summary>Quantos blocos de cache L3 (CCDs na AMD).</summary>
    public int CacheGroups { get; }

    public CpuTopology(IEnumerable<CpuSetInfo> sets)
    {
        Sets = sets.OrderBy(s => s.Group).ThenBy(s => s.LogicalIndex).ToList();
        Cores = Sets
            .GroupBy(s => (s.Group, s.CoreIndex))
            .OrderBy(g => g.Key.Group).ThenBy(g => g.Key.CoreIndex)
            .Select((g, i) => new CpuCore(i, g.Key.Group, g.First().LastLevelCacheIndex, g.First().EfficiencyClass,
                g.OrderBy(t => t.LogicalIndex).ToList()))
            .ToList();
        IsHybrid = Sets.Select(s => s.EfficiencyClass).Distinct().Count() > 1;
        CacheGroups = Sets.Select(s => (s.Group, s.LastLevelCacheIndex)).Distinct().Count();
    }

    public IReadOnlyList<uint> AllIds => Sets.Select(s => s.Id).ToList();

    /// <summary>
    /// CPU Sets para onde vão os apps de fundo. Lista vazia = não restringir
    /// (CPU pequena demais para separar sem prejudicar o jogo).
    /// </summary>
    public IReadOnlyList<uint> BackgroundIds(BackgroundCores mode)
    {
        if (mode == BackgroundCores.All || Cores.Count < 4) return [];

        if (mode == BackgroundCores.Auto && IsHybrid)
        {
            // Núcleos E têm a menor classe de eficiência.
            var eClass = Cores.Min(c => c.EfficiencyClass);
            return Cores.Where(c => c.EfficiencyClass == eClass).SelectMany(c => c.Ids).ToList();
        }

        var count = mode == BackgroundCores.LastTwoCores && Cores.Count >= 6 ? 2 : 1;
        return Cores.TakeLast(count).SelectMany(c => c.Ids).ToList();
    }

    /// <summary>CPU Sets do jogo. Lista vazia = sem restrição (todos os núcleos).</summary>
    public IReadOnlyList<uint> GameIds(GameCores mode, BackgroundCores backgroundMode)
    {
        if (mode == GameCores.All) return [];
        var background = BackgroundIds(backgroundMode).ToHashSet();
        return background.Count == 0 ? [] : Sets.Select(s => s.Id).Where(id => !background.Contains(id)).ToList();
    }

    public static unsafe CpuTopology FromSystem()
    {
        Kernel32.GetSystemCpuSetInformation(null, 0, out var needed, 0, 0);
        var buffer = new byte[needed];
        fixed (byte* p = buffer)
        {
            if (!Kernel32.GetSystemCpuSetInformation(p, needed, out needed, 0, 0))
                throw new System.ComponentModel.Win32Exception();
        }
        return new CpuTopology(Parse(buffer.AsSpan(0, (int)needed)));
    }

    /// <summary>Lê o array de SYSTEM_CPU_SET_INFORMATION (tamanho variável, campo Size em cada item).</summary>
    internal static List<CpuSetInfo> Parse(ReadOnlySpan<byte> data)
    {
        var result = new List<CpuSetInfo>();
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            if (size <= 0) break;
            if (type == 0 && offset + 20 <= data.Length) // CpuSetInformation
            {
                var item = data.Slice(offset, size);
                result.Add(new CpuSetInfo(
                    Id: BinaryPrimitives.ReadUInt32LittleEndian(item[8..]),
                    Group: BinaryPrimitives.ReadUInt16LittleEndian(item[12..]),
                    LogicalIndex: item[14],
                    CoreIndex: item[15],
                    LastLevelCacheIndex: item[16],
                    EfficiencyClass: item[18]));
            }
            offset += size;
        }
        return result;
    }
}
