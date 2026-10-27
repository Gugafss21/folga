using Folga.Core.Native;
using Folga.Core.Processes;
using Folga.Core.Profiles;

namespace Folga.Core.Memory;

/// <summary>Estado da RAM. Free = listas free + zero (o que está realmente vago); Standby = cache que o Windows pode reaproveitar.</summary>
public readonly record struct MemorySnapshot(long TotalBytes, long AvailableBytes, long FreeBytes, long StandbyBytes)
{
    public long UsedBytes => TotalBytes - AvailableBytes;
}

public interface ISystemMemory
{
    MemorySnapshot Read();
    bool PurgeStandbyList();
}

public sealed unsafe class WindowsSystemMemory : ISystemMemory
{
    public MemorySnapshot Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        Kernel32.GlobalMemoryStatusEx(ref status);
        long total = (long)status.ullTotalPhys, available = (long)status.ullAvailPhys;

        SYSTEM_MEMORY_LIST_INFORMATION info;
        if (Ntdll.NtQuerySystemInformation(Ntdll.SystemMemoryListInformation, &info, (uint)sizeof(SYSTEM_MEMORY_LIST_INFORMATION), out _) < 0)
            return new MemorySnapshot(total, available, available, 0); // sem privilégio: não sabemos separar

        long page = Environment.SystemPageSize;
        long standby = 0;
        for (var i = 0; i < 8; i++) standby += (long)info.PageCountByPriority[i];
        var free = ((long)info.FreePageCount + (long)info.ZeroPageCount) * page;
        return new MemorySnapshot(total, available, free, standby * page);
    }

    public bool PurgeStandbyList()
    {
        var command = Ntdll.MemoryPurgeStandbyList;
        return Ntdll.NtSetSystemInformation(Ntdll.SystemMemoryListInformation, &command, sizeof(int)) >= 0;
    }
}

public static class StandbyPolicy
{
    /// <summary>Limpar só quando a RAM vaga está baixa e há cache suficiente para valer a pena, respeitando o intervalo mínimo.</summary>
    public static bool ShouldPurge(MemorySnapshot snapshot, MemorySettings settings, DateTimeOffset now, DateTimeOffset? lastPurge)
    {
        if (!settings.PurgeStandbyList) return false;
        if (lastPurge is { } last && now - last < TimeSpan.FromSeconds(settings.PurgeCooldownSeconds)) return false;
        return snapshot.FreeBytes < settings.FreeThresholdMb * Mb && snapshot.StandbyBytes > settings.StandbyMinimumMb * Mb;
    }

    private const long Mb = 1024 * 1024;
}

public static class MemoryGovernor
{
    /// <summary>
    /// Escolhe quais processos esvaziar para o app voltar ao orçamento: os maiores primeiro,
    /// até que o que sobra (sem eles) caiba no orçamento.
    /// </summary>
    public static IReadOnlyList<ProcessRef> SelectTrimTargets(IReadOnlyList<(ProcessRef Process, long Bytes)> samples, long budgetBytes)
    {
        var total = samples.Sum(s => s.Bytes);
        if (budgetBytes <= 0 || total <= budgetBytes) return [];

        var targets = new List<ProcessRef>();
        foreach (var (process, bytes) in samples.OrderByDescending(s => s.Bytes))
        {
            if (total <= budgetBytes) break;
            targets.Add(process);
            total -= bytes;
        }
        return targets;
    }
}
