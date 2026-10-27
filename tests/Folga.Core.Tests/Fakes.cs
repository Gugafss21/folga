using Folga.Core.Detection;
using Folga.Core.Memory;
using Folga.Core.Processes;

namespace Folga.Core.Tests;

/// <summary>Processos de mentira com o estado de cada ChangeKind, para testar o motor sem tocar no sistema.</summary>
internal sealed class FakeProcessControl : IProcessControl
{
    public sealed class FakeProcess(int pid, string exe, long start)
    {
        public int Pid { get; } = pid;
        public string Exe { get; } = exe;
        public long Start { get; } = start;
        public bool DenyAccess { get; set; }
        public long WorkingSet { get; set; } = 100L << 20;
        public int Trims { get; set; }
        public Dictionary<ChangeKind, long[]> State { get; } = new()
        {
            [ChangeKind.Priority] = [0x20],
            [ChangeKind.CpuSets] = [],
            [ChangeKind.PowerThrottling] = [0, 0],
            [ChangeKind.IoPriority] = [2],
            [ChangeKind.MemoryPriority] = [5],
            [ChangeKind.WorkingSetLimit] = [204800, 1413120, 0],
        };
    }

    private long _nextStart = 1000;
    public Dictionary<int, FakeProcess> Processes { get; } = [];

    public FakeProcess Add(int pid, string exe)
    {
        var p = new FakeProcess(pid, exe, _nextStart++);
        Processes[pid] = p;
        return p;
    }

    public void Kill(int pid) => Processes.Remove(pid);

    public IReadOnlyList<ProcessEntry> Snapshot() => Processes.Values.Select(p => new ProcessEntry(p.Pid, p.Exe)).ToList();

    public ProcessRef? Resolve(int pid, string exeName) =>
        Processes.TryGetValue(pid, out var p) ? new ProcessRef(pid, exeName, p.Start) : null;

    public bool IsAlive(ProcessRef process) => Find(process) is not null;

    public TweakResult Read(ProcessRef process, ChangeKind kind, out long[] value)
    {
        value = [];
        if (Find(process) is not { } p) return TweakResult.Gone;
        if (p.DenyAccess) return TweakResult.AccessDenied;
        value = p.State[kind];
        return TweakResult.Ok;
    }

    public TweakResult Write(ProcessRef process, ChangeKind kind, long[] value)
    {
        if (Find(process) is not { } p) return TweakResult.Gone;
        if (p.DenyAccess) return TweakResult.AccessDenied;
        p.State[kind] = value;
        return TweakResult.Ok;
    }

    public long? PrivateWorkingSet(ProcessRef process) => Find(process)?.WorkingSet;

    public bool TrimWorkingSet(ProcessRef process)
    {
        if (Find(process) is not { } p) return false;
        p.Trims++;
        p.WorkingSet = 10L << 20;
        return true;
    }

    private FakeProcess? Find(ProcessRef r) =>
        Processes.TryGetValue(r.Pid, out var p) && p.Start == r.StartTime ? p : null;
}

internal sealed class FakeIfeo : IIfeoStore
{
    public Dictionary<string, int> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int? Get(string exeName) => Values.TryGetValue(exeName, out var v) ? v : null;
    public void Set(string exeName, int? value)
    {
        if (value is int v) Values[exeName] = v;
        else Values.Remove(exeName);
    }
}

internal sealed class FakeMemory : ISystemMemory
{
    public MemorySnapshot Snapshot { get; set; } = new(16L << 30, 8L << 30, 4L << 30, 2L << 30);
    public int Purges { get; private set; }
    public MemorySnapshot Read() => Snapshot;
    public bool PurgeStandbyList() { Purges++; return true; }
}

internal sealed class FakeWatcher : IProcessWatcher
{
    public event Action<int, string>? Started;
    public event Action<int>? Stopped;
    public void Start() { }
    public void Dispose() { }
    public void RaiseStarted(int pid, string exe) => Started?.Invoke(pid, exe);
    public void RaiseStopped(int pid) => Stopped?.Invoke(pid);
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "folga-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}
