using Folga.Core.Processes;
using Folga.Core.Profiles;

namespace Folga.Core.Diagnostics;

/// <summary>Estado real de um grupo de processos, lido de volta das APIs (não do que o app acha que aplicou).</summary>
public sealed record BackgroundGroupStatus(
    string RuleName,
    int ProcessCount,
    long WorkingSetBytes,
    int BudgetMb,
    string Priority,
    string EcoQos,
    string Cores,
    string MemoryPriority);

public sealed class BackgroundInspector(IProcessControl control)
{
    public IReadOnlyList<BackgroundGroupStatus> Inspect(FolgaConfig config)
    {
        var groups = new Dictionary<BackgroundRule, List<ProcessRef>>();
        foreach (var p in control.Snapshot())
        {
            if (config.FindGame(p.ExeName) is not null || config.FindRule(p.ExeName) is not { } rule) continue;
            if (control.Resolve(p.Pid, p.ExeName) is not { } process) continue;
            if (!groups.TryGetValue(rule, out var list)) groups[rule] = list = [];
            list.Add(process);
        }

        return config.Background
            .Where(r => r.Enabled)
            .Select(rule =>
            {
                var processes = groups.GetValueOrDefault(rule) ?? [];
                var ws = processes.Sum(p => control.PrivateWorkingSet(p) ?? 0);
                // O primeiro processo que der para ler representa o grupo.
                var sample = processes.FirstOrDefault(p => control.Read(p, ChangeKind.Priority, out _) == TweakResult.Ok);
                return new BackgroundGroupStatus(rule.Name, processes.Count, ws, rule.MemoryBudgetMb,
                    sample is null ? "—" : DescribePriority(sample),
                    sample is null ? "—" : DescribeEcoQos(sample),
                    sample is null ? "—" : DescribeCores(sample),
                    sample is null ? "—" : DescribeMemoryPriority(sample));
            })
            .ToList();
    }

    private string DescribePriority(ProcessRef p) =>
        control.Read(p, ChangeKind.Priority, out var v) == TweakResult.Ok ? PriorityMapping.Label(PriorityMapping.FromWin32(v[0])) : "—";

    private string DescribeEcoQos(ProcessRef p) =>
        control.Read(p, ChangeKind.PowerThrottling, out var v) == TweakResult.Ok
            ? (v[0], v[1]) switch { (1, 1) => "Ligado", (1, 0) => "Desligado", _ => "Automático" }
            : "—";

    private string DescribeCores(ProcessRef p) =>
        control.Read(p, ChangeKind.CpuSets, out var v) == TweakResult.Ok
            ? v.Length == 0 ? "Todos" : $"{v.Length} threads"
            : "—";

    private string DescribeMemoryPriority(ProcessRef p) =>
        control.Read(p, ChangeKind.MemoryPriority, out var v) == TweakResult.Ok
            ? v[0] switch { 1 => "Muito baixa", 2 => "Baixa", 3 => "Média", 4 => "Abaixo do normal", 5 => "Normal", _ => v[0].ToString() }
            : "—";
}
