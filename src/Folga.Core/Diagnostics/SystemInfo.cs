using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Folga.Core.Diagnostics;

public sealed record RamInfo(int ConfiguredMhz, int RatedMhz, int MemoryType, int Modules)
{
    private const int Ddr4 = 26;

    /// <summary>DDR4 rodando a 2666 MHz ou menos quase sempre é XMP/DOCP desligado na BIOS.</summary>
    public string? Warning => MemoryType == Ddr4 && ConfiguredMhz is > 0 and <= 2666
        ? $"RAM a {ConfiguredMhz} MHz: confira se o DOCP/XMP está ativo na BIOS."
        : null;
}

public static partial class SystemInfo
{
    private const uint RSMB = 0x52534D42; // 'RSMB': tabela SMBIOS crua
    private const byte MemoryDevice = 17;

    /// <summary>
    /// Velocidade da RAM lida direto da tabela SMBIOS (estrutura 17, "Memory Device").
    /// Mesmo dado do WMI Win32_PhysicalMemory, sem carregar o WMI (~14 MB a menos no app).
    /// </summary>
    public static RamInfo? ReadRam()
    {
        var size = GetSystemFirmwareTable(RSMB, 0, null, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        if (GetSystemFirmwareTable(RSMB, 0, buffer, size) != size) return null;
        return ParseRam(buffer.AsSpan(8)); // RawSMBIOSData: 8 bytes de cabeçalho antes das estruturas
    }

    internal static RamInfo? ParseRam(ReadOnlySpan<byte> table)
    {
        int configured = int.MaxValue, rated = int.MaxValue, type = 0, modules = 0;
        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            var kind = table[offset];
            var length = table[offset + 1];
            if (length < 4 || offset + length > table.Length) break;
            var s = table.Slice(offset, length);

            if (kind == MemoryDevice && length >= 0x17)
            {
                var sizeField = BinaryPrimitives.ReadUInt16LittleEndian(s[0x0C..]);
                if (sizeField is not 0 and not 0xFFFF) // 0 = slot vazio
                {
                    modules++;
                    type = s[0x12];
                    var speed = BinaryPrimitives.ReadUInt16LittleEndian(s[0x15..]);
                    if (speed > 0) rated = Math.Min(rated, speed);
                    if (length >= 0x22)
                    {
                        var conf = BinaryPrimitives.ReadUInt16LittleEndian(s[0x20..]);
                        if (conf > 0) configured = Math.Min(configured, conf);
                    }
                }
            }
            if (kind == 127) break; // fim da tabela

            // Depois da área formatada vêm as strings, terminadas por dois zeros.
            var next = offset + length;
            while (next + 1 < table.Length && (table[next] != 0 || table[next + 1] != 0)) next++;
            offset = next + 2;
        }
        if (modules == 0) return null;
        return new RamInfo(configured == int.MaxValue ? 0 : configured, rated == int.MaxValue ? 0 : rated, type, modules);
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetSystemFirmwareTable(uint provider, uint tableId, [Out] byte[]? buffer, uint size);
}
