using System.Diagnostics;
using Folga.Core.Memory;
using Folga.Core.Processes;

namespace Folga.Core.Tests;

/// <summary>Testes contra o Windows de verdade, num processo filho nosso (não precisa de admin).</summary>
public sealed class WindowsProcessControlTests : IDisposable
{
    private readonly Process _child;
    private readonly WindowsProcessControl _control = new();
    private readonly ProcessRef _ref;

    public WindowsProcessControlTests()
    {
        _child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        _ref = _control.Resolve(_child.Id, "cmd.exe") ?? throw new InvalidOperationException("filho não resolvido");
    }

    public void Dispose()
    {
        try { _child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _child.Dispose();
    }

    [Theory]
    [InlineData(ChangeKind.Priority, new long[] { 0x4000 })]
    [InlineData(ChangeKind.PowerThrottling, new long[] { 1, 1 })]
    [InlineData(ChangeKind.IoPriority, new long[] { 1 })]
    [InlineData(ChangeKind.MemoryPriority, new long[] { 2 })]
    public void Aplica_le_de_volta_e_desfaz(ChangeKind kind, long[] value)
    {
        Assert.Equal(TweakResult.Ok, _control.Read(_ref, kind, out var original));
        Assert.Equal(TweakResult.Ok, _control.Write(_ref, kind, value));
        Assert.Equal(TweakResult.Ok, _control.Read(_ref, kind, out var applied));
        // A leitura do EcoQoS não existe em todo Windows; quando falha o original é [0, 0].
        if (kind != ChangeKind.PowerThrottling || applied is not [0, 0]) Assert.Equal(value, applied);
        Assert.Equal(TweakResult.Ok, _control.Write(_ref, kind, original));
    }

    [Fact]
    public void CPU_Sets_restringe_e_limpa()
    {
        var topology = Cpu.CpuTopology.FromSystem();
        var last = topology.Cores[^1].Ids.Select(i => (long)i).ToArray();

        Assert.Equal(TweakResult.Ok, _control.Write(_ref, ChangeKind.CpuSets, last));
        Assert.Equal(TweakResult.Ok, _control.Read(_ref, ChangeKind.CpuSets, out var applied));
        Assert.Equal(last, applied);

        Assert.Equal(TweakResult.Ok, _control.Write(_ref, ChangeKind.CpuSets, []));
        Assert.Equal(TweakResult.Ok, _control.Read(_ref, ChangeKind.CpuSets, out var cleared));
        Assert.Empty(cleared);
    }

    [Fact]
    public void Le_e_esvazia_working_set()
    {
        Assert.True(_control.PrivateWorkingSet(_ref) > 0);
        Assert.True(_control.TrimWorkingSet(_ref));
    }

    [Fact]
    public void Processo_morto_vira_Gone()
    {
        _child.Kill(entireProcessTree: true);
        _child.WaitForExit();
        Assert.False(_control.IsAlive(_ref));
        Assert.Equal(TweakResult.Gone, _control.Write(_ref, ChangeKind.Priority, [0x20]));
    }

    [Fact]
    public void PID_com_horario_diferente_vira_Gone()
    {
        var wrong = _ref with { StartTime = _ref.StartTime + 1 };
        Assert.Equal(TweakResult.Gone, _control.Write(wrong, ChangeKind.Priority, [0x4000]));
    }

    [Fact]
    public void Le_a_memoria_do_sistema()
    {
        var snapshot = new WindowsSystemMemory().Read();
        Assert.True(snapshot.TotalBytes > 0);
        Assert.InRange(snapshot.AvailableBytes, 1, snapshot.TotalBytes);
    }
}

public sealed class SuspendTests
{
    [Fact]
    public void Congela_e_descongela_um_processo_real()
    {
        var control = new WindowsProcessControl();
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var r = control.Resolve(child.Id, "cmd.exe")!;
            Assert.Equal(TweakResult.Ok, control.Write(r, ChangeKind.Suspended, [1]));
            child.Refresh();
            Assert.All(child.Threads.Cast<ProcessThread>(), t => Assert.Equal(ThreadWaitReason.Suspended, t.WaitReason));
            Assert.Equal(TweakResult.Ok, control.Write(r, ChangeKind.Suspended, [0]));
            child.Refresh();
            Assert.DoesNotContain(child.Threads.Cast<ProcessThread>(), t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }
}
