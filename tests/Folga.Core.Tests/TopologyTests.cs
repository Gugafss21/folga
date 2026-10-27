using System.Buffers.Binary;
using Folga.Core.Cpu;
using Folga.Core.Profiles;

namespace Folga.Core.Tests;

public sealed class TopologyTests
{
    /// <summary>6 núcleos × 2 threads, um L3, sem núcleos E. Ids como o Windows costuma dar (256 + índice lógico).</summary>
    internal static CpuTopology Ryzen5500X3D() =>
        new(Enumerable.Range(0, 12).Select(i => new CpuSetInfo((uint)(256 + i), 0, (byte)i, (byte)(i / 2 * 2), 0, 0)));

    /// <summary>Intel 12600K: 6 P com HT (classe 1) + 4 E (classe 0).</summary>
    private static CpuTopology Intel12600K() =>
        new(Enumerable.Range(0, 12).Select(i => new CpuSetInfo((uint)(256 + i), 0, (byte)i, (byte)(i / 2 * 2), 0, 1))
            .Concat(Enumerable.Range(12, 4).Select(i => new CpuSetInfo((uint)(256 + i), 0, (byte)i, (byte)i, 0, 0))));

    /// <summary>Ryzen 7950X3D: 2 CCDs de 8 núcleos com HT.</summary>
    private static CpuTopology Ryzen7950X3D() =>
        new(Enumerable.Range(0, 32).Select(i => new CpuSetInfo((uint)(256 + i), 0, (byte)i, (byte)(i / 2 * 2), (byte)(i / 16), 0)));

    [Fact]
    public void Ryzen_5500X3D_vira_6_nucleos_e_o_fundo_fica_no_ultimo()
    {
        var t = Ryzen5500X3D();
        Assert.Equal(6, t.Cores.Count);
        Assert.False(t.IsHybrid);
        Assert.Equal(1, t.CacheGroups);
        Assert.Equal([266u, 267u], t.BackgroundIds(BackgroundCores.Auto));
        Assert.Equal([264u, 265u, 266u, 267u], t.BackgroundIds(BackgroundCores.LastTwoCores));
        Assert.Empty(t.BackgroundIds(BackgroundCores.All));
        Assert.Empty(t.GameIds(GameCores.All, BackgroundCores.Auto));
        Assert.Equal(10, t.GameIds(GameCores.ExcludeBackground, BackgroundCores.Auto).Count);
    }

    [Fact]
    public void CPU_hibrida_manda_o_fundo_para_os_nucleos_E()
    {
        var t = Intel12600K();
        Assert.True(t.IsHybrid);
        Assert.Equal(10, t.Cores.Count);
        Assert.Equal([268u, 269u, 270u, 271u], t.BackgroundIds(BackgroundCores.Auto));
        Assert.Equal(12, t.GameIds(GameCores.ExcludeBackground, BackgroundCores.Auto).Count);
    }

    [Fact]
    public void Dual_CCD_conta_dois_blocos_de_cache()
    {
        var t = Ryzen7950X3D();
        Assert.Equal(16, t.Cores.Count);
        Assert.Equal(2, t.CacheGroups);
        Assert.Equal([286u, 287u], t.BackgroundIds(BackgroundCores.Auto));
    }

    [Fact]
    public void CPU_pequena_nao_restringe()
    {
        var t = new CpuTopology(Enumerable.Range(0, 4).Select(i => new CpuSetInfo((uint)i, 0, (byte)i, (byte)(i / 2 * 2), 0, 0)));
        Assert.Empty(t.BackgroundIds(BackgroundCores.Auto));
    }

    [Fact]
    public void Parse_le_o_buffer_do_Windows()
    {
        var buffer = new byte[64];
        for (var i = 0; i < 2; i++)
        {
            var item = buffer.AsSpan(i * 32, 32);
            BinaryPrimitives.WriteUInt32LittleEndian(item, 32);
            BinaryPrimitives.WriteUInt32LittleEndian(item[4..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(item[8..], (uint)(256 + i));
            item[14] = (byte)i;
            item[15] = 0;
            item[18] = 1;
        }
        var sets = CpuTopology.Parse(buffer);
        Assert.Equal(2, sets.Count);
        Assert.Equal(257u, sets[1].Id);
        Assert.Equal(1, sets[1].LogicalIndex);
        Assert.Equal(1, sets[1].EfficiencyClass);
    }

    [Fact]
    public void Topologia_desta_maquina_e_lida()
    {
        var t = CpuTopology.FromSystem();
        Assert.Equal(Environment.ProcessorCount, t.Sets.Count);
        Assert.NotEmpty(t.Cores);
    }
}

public sealed class SystemInfoTests
{
    [Fact]
    public void Le_a_RAM_pela_SMBIOS_desta_maquina()
    {
        var ram = Diagnostics.SystemInfo.ReadRam();
        Assert.NotNull(ram);
        Assert.True(ram.Modules > 0);
        Assert.True(ram.ConfiguredMhz > 0);
    }

    [Fact]
    public void DDR4_lenta_gera_aviso()
    {
        Assert.NotNull(new Diagnostics.RamInfo(2133, 3200, 26, 2).Warning);
        Assert.Null(new Diagnostics.RamInfo(3200, 3200, 26, 2).Warning);
        Assert.Null(new Diagnostics.RamInfo(4800, 4800, 34, 2).Warning); // DDR5
    }
}
