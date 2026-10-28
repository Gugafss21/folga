namespace Folga.Core.Diagnostics;

public sealed record RamInfo(int ConfiguredMhz, int RatedMhz, int MemoryType, int Modules)
{
    private const int Ddr4 = 26;

    /// <summary>DDR4 rodando a 2666 MHz ou menos quase sempre é XMP/DOCP desligado na BIOS.</summary>
    public string? Warning => MemoryType == Ddr4 && ConfiguredMhz is > 0 and <= 2666
        ? $"RAM a {ConfiguredMhz} MHz: confira se o DOCP/XMP está ativo na BIOS."
        : null;
}

public static class SystemInfo
{
    /// <summary>
    /// Velocidade da RAM pela tabela SMBIOS (a mesma leitura dos pentes da tela Hardware): o pente mais lento
    /// manda. Mesmo dado do WMI Win32_PhysicalMemory, sem carregar o WMI (~14 MB a menos no app).
    /// </summary>
    public static RamInfo? ReadRam() => Summarize(HardwareInfo.ReadMemory());

    internal static RamInfo? Summarize(IReadOnlyList<MemoryModule> modules)
    {
        if (modules.Count == 0) return null;
        static int Slowest(IEnumerable<int> speeds) => speeds.Where(s => s > 0).DefaultIfEmpty(0).Min();
        return new RamInfo(Slowest(modules.Select(m => m.ConfiguredMhz)), Slowest(modules.Select(m => m.RatedMhz)), modules[^1].Type, modules.Count);
    }
}
