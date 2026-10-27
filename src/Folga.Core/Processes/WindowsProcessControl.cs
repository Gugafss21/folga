using System.Diagnostics;
using System.Runtime.InteropServices;
using Folga.Core.Native;
using static Folga.Core.Native.Kernel32;

namespace Folga.Core.Processes;

public sealed unsafe class WindowsProcessControl : IProcessControl
{
    private const uint ReadAccess = PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_QUERY_INFORMATION;
    private const uint WriteAccess = PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION | PROCESS_SET_LIMITED_INFORMATION | PROCESS_SET_QUOTA;

    private readonly int _ownPid = Environment.ProcessId;

    public IReadOnlyList<ProcessEntry> Snapshot()
    {
        var result = new List<ProcessEntry>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4 || p.Id == _ownPid) continue;
                try { result.Add(new ProcessEntry(p.Id, p.ProcessName + ".exe")); }
                catch (InvalidOperationException) { } // saiu enquanto listávamos
            }
        }
        return result;
    }

    public ProcessRef? Resolve(int pid, string exeName)
    {
        if (pid <= 4 || pid == _ownPid) return null;
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid || !GetProcessTimes(h, out var creation, out var exit, out _, out _) || exit != 0) return null;
        return new ProcessRef(pid, exeName, creation);
    }

    public bool IsAlive(ProcessRef process)
    {
        using var h = OpenChecked(process, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_QUERY_LIMITED_INFORMATION, out var status);
        return status == TweakResult.Ok;
    }

    public TweakResult Read(ProcessRef process, ChangeKind kind, out long[] value)
    {
        value = [];
        using var h = OpenChecked(process, ReadAccess, PROCESS_QUERY_LIMITED_INFORMATION, out var status);
        if (status != TweakResult.Ok) return status;

        switch (kind)
        {
            case ChangeKind.Priority:
            {
                var cls = GetPriorityClass(h);
                if (cls == 0) return LastError();
                value = [cls];
                return TweakResult.Ok;
            }
            case ChangeKind.CpuSets:
            {
                GetProcessDefaultCpuSets(h, null, 0, out var required);
                if (required == 0) { value = []; return TweakResult.Ok; }
                var ids = new uint[required];
                fixed (uint* p = ids)
                    if (!GetProcessDefaultCpuSets(h, p, required, out required)) return LastError();
                value = ids.Take((int)required).Select(i => (long)i).ToArray();
                return TweakResult.Ok;
            }
            case ChangeKind.PowerThrottling:
            {
                var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1 };
                // Nem toda versão do Windows deixa ler; nesse caso o original é "o sistema decide".
                value = GetProcessInformation(h, ProcessPowerThrottling, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE))
                    ? [state.ControlMask & 1, state.StateMask & 1]
                    : [0, 0];
                return TweakResult.Ok;
            }
            case ChangeKind.IoPriority:
            {
                int io;
                var nt = Ntdll.NtQueryInformationProcess(h, Ntdll.ProcessIoPriority, &io, sizeof(int), out _);
                if (nt < 0) return NtResult(nt);
                value = [io];
                return TweakResult.Ok;
            }
            case ChangeKind.MemoryPriority:
            {
                uint mem;
                if (!GetProcessInformation(h, ProcessMemoryPriority, &mem, sizeof(uint))) return LastError();
                value = [mem];
                return TweakResult.Ok;
            }
            case ChangeKind.WorkingSetLimit:
            {
                if (!GetProcessWorkingSetSizeEx(h, out var min, out var max, out var flags)) return LastError();
                value = [min, max, flags];
                return TweakResult.Ok;
            }
            default:
                return TweakResult.Failed;
        }
    }

    public TweakResult Write(ProcessRef process, ChangeKind kind, long[] value)
    {
        using var h = OpenChecked(process, WriteAccess, WriteAccessFor(kind), out var status);
        if (status != TweakResult.Ok) return status;

        switch (kind)
        {
            case ChangeKind.Priority:
                return SetPriorityClass(h, (uint)value[0]) ? TweakResult.Ok : LastError();

            case ChangeKind.CpuSets:
            {
                if (value.Length == 0) return SetProcessDefaultCpuSets(h, null, 0) ? TweakResult.Ok : LastError();
                var ids = value.Select(v => (uint)v).ToArray();
                fixed (uint* p = ids)
                    return SetProcessDefaultCpuSets(h, p, (uint)ids.Length) ? TweakResult.Ok : LastError();
            }
            case ChangeKind.PowerThrottling:
            {
                var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = (uint)value[0], StateMask = (uint)value[1] };
                return SetProcessInformation(h, ProcessPowerThrottling, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE))
                    ? TweakResult.Ok : LastError();
            }
            case ChangeKind.IoPriority:
            {
                var io = (int)value[0];
                return NtResult(Ntdll.NtSetInformationProcess(h, Ntdll.ProcessIoPriority, &io, sizeof(int)));
            }
            case ChangeKind.MemoryPriority:
            {
                var mem = (uint)value[0];
                return SetProcessInformation(h, ProcessMemoryPriority, &mem, sizeof(uint)) ? TweakResult.Ok : LastError();
            }
            case ChangeKind.WorkingSetLimit:
            {
                var flags = (uint)value[2];
                // Valor original sem teto: desliga explicitamente os limites rígidos.
                if ((flags & QUOTA_LIMITS_HARDWS_MAX_ENABLE) == 0) flags |= QUOTA_LIMITS_HARDWS_MAX_DISABLE;
                if ((flags & QUOTA_LIMITS_HARDWS_MIN_ENABLE) == 0) flags |= QUOTA_LIMITS_HARDWS_MIN_DISABLE;
                return SetProcessWorkingSetSizeEx(h, (nint)value[0], (nint)value[1], flags) ? TweakResult.Ok : LastError();
            }
            default:
                return TweakResult.Failed;
        }
    }

    public long? PrivateWorkingSet(ProcessRef process)
    {
        using var h = OpenChecked(process, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_QUERY_LIMITED_INFORMATION, out var status);
        if (status != TweakResult.Ok) return null;
        var counters = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX2) };
        if (!K32GetProcessMemoryInfo(h, &counters, counters.cb)) return null;
        return counters.PrivateWorkingSetSize != 0 ? (long)counters.PrivateWorkingSetSize : (long)counters.WorkingSetSize;
    }

    public bool TrimWorkingSet(ProcessRef process)
    {
        using var h = OpenChecked(process, PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, out var status);
        return status == TweakResult.Ok && K32EmptyWorkingSet(h);
    }

    /// <summary>Caminho completo do .exe (para a tela de jogos); null se não der para ler.</summary>
    public static string? ImagePath(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return null;
        var buffer = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    private static uint WriteAccessFor(ChangeKind kind) => PROCESS_QUERY_LIMITED_INFORMATION | kind switch
    {
        ChangeKind.CpuSets => PROCESS_SET_LIMITED_INFORMATION,
        ChangeKind.WorkingSetLimit => PROCESS_SET_QUOTA,
        _ => PROCESS_SET_INFORMATION,
    };

    /// <summary>Abre o processo (tentando primeiro o acesso amplo) e confere se ainda é o mesmo pelo horário de criação.</summary>
    private static ProcessHandle OpenChecked(ProcessRef process, uint preferred, uint fallback, out TweakResult status)
    {
        var h = OpenProcess(preferred, false, process.Pid);
        if (h.IsInvalid && fallback != preferred)
        {
            h.Dispose();
            h = OpenProcess(fallback, false, process.Pid);
        }
        if (h.IsInvalid)
        {
            status = Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED ? TweakResult.AccessDenied : TweakResult.Gone;
            return h;
        }
        // Um processo que já saiu continua abrível enquanto alguém tiver handle dele; o horário de saída denuncia.
        status = GetProcessTimes(h, out var creation, out var exit, out _, out _) && creation == process.StartTime && exit == 0
            ? TweakResult.Ok
            : TweakResult.Gone;
        return h;
    }

    private static TweakResult LastError() =>
        Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED ? TweakResult.AccessDenied : TweakResult.Failed;

    private static TweakResult NtResult(int status) => status switch
    {
        >= 0 => TweakResult.Ok,
        unchecked((int)0xC0000022) => TweakResult.AccessDenied, // STATUS_ACCESS_DENIED
        _ => TweakResult.Failed,
    };
}
