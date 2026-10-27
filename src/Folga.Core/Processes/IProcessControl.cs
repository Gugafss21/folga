using Folga.Core.Profiles;

namespace Folga.Core.Processes;

/// <summary>Processo identificado por PID + horário de criação, para não confundir PID reaproveitado.</summary>
public sealed record ProcessRef(int Pid, string ExeName, long StartTime);

public readonly record struct ProcessEntry(int Pid, string ExeName);

/// <summary>O que o app muda no sistema. Cada tipo tem uma codificação numérica do valor (ver <see cref="IProcessControl"/>).</summary>
public enum ChangeKind
{
    /// <summary>[classe de prioridade Win32]</summary>
    Priority,
    /// <summary>[ids dos CPU Sets...]; vazio = sem restrição</summary>
    CpuSets,
    /// <summary>[ControlMask, StateMask] de PROCESS_POWER_THROTTLING_EXECUTION_SPEED (EcoQoS)</summary>
    PowerThrottling,
    /// <summary>[0..3] muito baixa, baixa, normal, alta</summary>
    IoPriority,
    /// <summary>[1..5] MEMORY_PRIORITY_*</summary>
    MemoryPriority,
    /// <summary>[mínimo, máximo, flags] do working set</summary>
    WorkingSetLimit,
    /// <summary>Valor CpuPriorityClass no IFEO; [] = não existia. Persistente: não é desfeito ao fechar o jogo.</summary>
    Ifeo,
}

public enum TweakResult { Ok, AccessDenied, Gone, Failed }

public interface IProcessControl
{
    IReadOnlyList<ProcessEntry> Snapshot();

    /// <summary>Lê o horário de criação; null se o processo não existe mais ou não pode ser aberto.</summary>
    ProcessRef? Resolve(int pid, string exeName);

    bool IsAlive(ProcessRef process);

    TweakResult Read(ProcessRef process, ChangeKind kind, out long[] value);

    TweakResult Write(ProcessRef process, ChangeKind kind, long[] value);

    /// <summary>Working set privado em bytes; null se não foi possível ler.</summary>
    long? PrivateWorkingSet(ProcessRef process);

    bool TrimWorkingSet(ProcessRef process);
}

public interface IIfeoStore
{
    /// <summary>CpuPriorityClass gravado para o .exe, ou null.</summary>
    int? Get(string exeName);

    /// <summary>Grava ou, com null, remove o valor (e as chaves que ficarem vazias).</summary>
    void Set(string exeName, int? value);
}

public static class PriorityMapping
{
    public const uint IDLE_PRIORITY_CLASS = 0x40;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    public const uint NORMAL_PRIORITY_CLASS = 0x20;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x8000;
    public const uint HIGH_PRIORITY_CLASS = 0x80;

    public static uint ToWin32(PriorityLevel level) => level switch
    {
        PriorityLevel.Idle => IDLE_PRIORITY_CLASS,
        PriorityLevel.BelowNormal => BELOW_NORMAL_PRIORITY_CLASS,
        PriorityLevel.AboveNormal => ABOVE_NORMAL_PRIORITY_CLASS,
        PriorityLevel.High => HIGH_PRIORITY_CLASS,
        _ => NORMAL_PRIORITY_CLASS,
    };

    public static PriorityLevel? FromWin32(long value) => value switch
    {
        IDLE_PRIORITY_CLASS => PriorityLevel.Idle,
        BELOW_NORMAL_PRIORITY_CLASS => PriorityLevel.BelowNormal,
        NORMAL_PRIORITY_CLASS => PriorityLevel.Normal,
        ABOVE_NORMAL_PRIORITY_CLASS => PriorityLevel.AboveNormal,
        HIGH_PRIORITY_CLASS => PriorityLevel.High,
        _ => null,
    };

    /// <summary>Valores do PerfOptions\CpuPriorityClass do IFEO.</summary>
    public static int ToIfeo(PriorityLevel level) => level switch
    {
        PriorityLevel.Idle => 1,
        PriorityLevel.BelowNormal => 5,
        PriorityLevel.AboveNormal => 6,
        PriorityLevel.High => 3,
        _ => 2,
    };

    public static string Label(PriorityLevel? level) => level switch
    {
        PriorityLevel.Idle => "Ociosa",
        PriorityLevel.BelowNormal => "Abaixo do normal",
        PriorityLevel.Normal => "Normal",
        PriorityLevel.AboveNormal => "Acima do normal",
        PriorityLevel.High => "Alta",
        _ => "—",
    };
}
