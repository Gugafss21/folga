using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Folga.Core.Diagnostics;

public sealed record CpuDetails(string Name, int Cores, int Threads, int L1Kb, int L2Kb, int L3Kb, int BaseMhz);

public sealed record BoardDetails(string? SystemMaker, string? SystemModel, string? BoardMaker, string? BoardModel, string? BiosVendor, string? BiosVersion, string? BiosDate);

/// <summary>
/// Um pente de memória pela tabela SMBIOS. RatedMhz é a velocidade padrão do pente (JEDEC); ConfiguredMhz, a que
/// a BIOS aplicou (acima da padrão = XMP/DOCP ou ajuste manual). Tensões em milivolts; MinMv é a padrão.
/// </summary>
public sealed record MemoryModule(string Slot, string? Maker, string? PartNumber, long SizeMb, int Type, int RatedMhz, int ConfiguredMhz,
    int? MinMv, int? ConfiguredMv, int Ranks)
{
    public string TypeName => Type switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", 30 => "LPDDR4", 35 => "LPDDR5", _ => "RAM" };

    /// <summary>Latência (CL) e velocidade de fábrica que o código do pente anuncia (perfil XMP), quando o formato é conhecido.</summary>
    public (int Mhz, int Cl)? Advertised => PartNumbers.Decode(PartNumber);
}

public sealed record StorageDetails(string Drive, string? Label, string? Model, string Kind, long TotalBytes, long FreeBytes, string? Format);

public sealed record OsDetails(string Name, string Version, string Build);

/// <summary>O que o código do pente diz sobre ele. Só formatos conhecidos; o resto fica sem CL.</summary>
public static partial class PartNumbers
{
    // Kingston Fury/HyperX: KF432C16BB/8 (DDR4, 3200, CL16; o primeiro dígito é a geração); G.Skill: F4-3200C16-8GVKB; Corsair: CMK16GX4M2B3200C16;
    // Crucial Ballistix: BL8G32C16U4B; TeamGroup: TF3D416G3200HC16FDC01; ADATA XPG: AX4U32008G16A.
    [GeneratedRegex(@"^(?:KF|HX)[345](?<mhz>\d{2})C(?<cl>\d{2})", RegexOptions.IgnoreCase)] private static partial Regex Kingston();
    [GeneratedRegex(@"^F[345]-(?<mhz>\d{4})[CJ](?<cl>\d{2})", RegexOptions.IgnoreCase)] private static partial Regex GSkill();
    [GeneratedRegex(@"^CM\w*?(?<mhz>\d{4})C(?<cl>\d{2})", RegexOptions.IgnoreCase)] private static partial Regex Corsair();
    [GeneratedRegex(@"^BL\d+G(?<mhz>\d{2})C(?<cl>\d{2})", RegexOptions.IgnoreCase)] private static partial Regex Ballistix();
    [GeneratedRegex(@"(?<mhz>\d{4})HC(?<cl>\d{2})", RegexOptions.IgnoreCase)] private static partial Regex Team();
    [GeneratedRegex(@"^AX[45]U(?<mhz>\d{4})\d+G(?<cl>\d{2})", RegexOptions.IgnoreCase)] private static partial Regex Adata();

    public static (int Mhz, int Cl)? Decode(string? part)
    {
        if (string.IsNullOrWhiteSpace(part)) return null;
        part = part.Trim();
        foreach (var (regex, scale) in new[] { (Kingston(), 100), (GSkill(), 1), (Corsair(), 1), (Ballistix(), 100), (Team(), 1), (Adata(), 1) })
        {
            var m = regex.Match(part);
            if (!m.Success) continue;
            var mhz = int.Parse(m.Groups["mhz"].Value) * scale;
            var cl = int.Parse(m.Groups["cl"].Value);
            if (mhz is >= 1600 and <= 9000 && cl is >= 8 and <= 60) return (mhz, cl);
        }
        return null;
    }
}

/// <summary>
/// Leitura do hardware só com o que o Windows já expõe para programas comuns (tabela SMBIOS, registro, consultas
/// de disco). Nada de driver: timings da memória em uso (CL real) e clocks internos ficam de fora de propósito.
/// </summary>
public static unsafe partial class HardwareInfo
{
    private const uint RSMB = 0x52534D42;

    public static CpuDetails? ReadCpu()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processador";
        var baseMhz = key?.GetValue("~MHz") is int mhz ? mhz : 0;

        int cores = 0, threads = 0, l1 = 0, l2 = 0, l3 = 0;
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationAll, null, ref length);
        if (length == 0) return new CpuDetails(name, 0, Environment.ProcessorCount, 0, 0, 0, baseMhz);
        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, p, ref length)) return new CpuDetails(name, 0, Environment.ProcessorCount, 0, 0, 0, baseMhz);
            var offset = 0;
            while (offset + 8 <= length)
            {
                var relation = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
                var size = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 4));
                if (size <= 0) break;
                if (relation == RelationProcessorCore)
                {
                    cores++;
                    // PROCESSOR_RELATIONSHIP: Flags, EfficiencyClass, Reserved[20], GroupCount, GroupMask[] (Mask de 8 bytes + Group).
                    var groupCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 8 + 22));
                    for (var g = 0; g < groupCount; g++)
                        threads += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset + 8 + 24 + g * 16)));
                }
                else if (relation == RelationCache)
                {
                    // CACHE_RELATIONSHIP: Level, Associativity, LineSize, CacheSize, Type.
                    var level = buffer[offset + 8];
                    var kb = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 8 + 4)) / 1024;
                    var type = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 8 + 8));
                    if (level == 1 && type is 1 or 2 or 0) l1 += kb; // dados + instruções
                    else if (level == 2) l2 += kb;
                    else if (level == 3) l3 += kb;
                }
                offset += size;
            }
        }
        return new CpuDetails(name, cores, threads, l1, l2, l3, baseMhz);
    }

    public static BoardDetails ReadBoard()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string? V(string name) => (key?.GetValue(name) as string)?.Trim() is { Length: > 0 } s && !IsPlaceholder(s) ? s : null;
        return new BoardDetails(V("SystemManufacturer"), V("SystemProductName"), V("BaseBoardManufacturer"), V("BaseBoardProduct"),
            V("BIOSVendor"), V("BIOSVersion"), V("BIOSReleaseDate"));
    }

    /// <summary>Fabricantes de placa às vezes deixam o texto padrão do firmware no lugar do nome.</summary>
    private static bool IsPlaceholder(string s) =>
        s.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) || s.Contains("Default string", StringComparison.OrdinalIgnoreCase)
        || s.Equals("System Product Name", StringComparison.OrdinalIgnoreCase) || s.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<MemoryModule> ReadMemory()
    {
        var size = GetSystemFirmwareTable(RSMB, 0, null, 0);
        if (size == 0) return [];
        var buffer = new byte[size];
        if (GetSystemFirmwareTable(RSMB, 0, buffer, size) != size) return [];
        return ParseMemory(buffer.AsSpan(8));
    }

    /// <summary>Estruturas 17 (Memory Device) da tabela SMBIOS, só os slots ocupados.</summary>
    internal static List<MemoryModule> ParseMemory(ReadOnlySpan<byte> table)
    {
        var result = new List<MemoryModule>();
        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            var kind = table[offset];
            var length = table[offset + 1];
            if (length < 4 || offset + length > table.Length) break;
            var s = table.Slice(offset, length);

            // As strings vêm depois da área formatada, numeradas a partir de 1, terminadas por dois zeros.
            var stringsStart = offset + length;
            var end = stringsStart;
            while (end + 1 < table.Length && (table[end] != 0 || table[end + 1] != 0)) end++;
            var strings = Strings(table[stringsStart..Math.Min(end + 1, table.Length)]);

            if (kind == 17 && length >= 0x1B)
            {
                var sizeField = BinaryPrimitives.ReadUInt16LittleEndian(s[0x0C..]);
                if (sizeField is not 0 and not 0xFFFF)
                {
                    long mb = sizeField == 0x7FFF && length >= 0x20 ? BinaryPrimitives.ReadUInt32LittleEndian(s[0x1C..])
                        : (sizeField & 0x8000) != 0 ? (sizeField & 0x7FFF) / 1024 : sizeField;
                    var slot = string.Join(" ", new[] { Str(s, strings, 0x11), Str(s, strings, 0x10) }.Where(x => x is not null).Distinct());
                    result.Add(new MemoryModule(
                        slot.Length > 0 ? slot : $"Pente {result.Count + 1}",
                        Clean(Str(s, strings, 0x17)), Clean(Str(s, strings, 0x1A)), mb, s[0x12],
                        BinaryPrimitives.ReadUInt16LittleEndian(s[0x15..]),
                        length >= 0x22 ? BinaryPrimitives.ReadUInt16LittleEndian(s[0x20..]) : 0,
                        Mv(s, 0x22), Mv(s, 0x26), s[0x1B] & 0x0F));
                }
            }
            if (kind == 127) break;
            offset = end + 2;
        }
        return result;
    }

    /// <summary>Campo de string da estrutura: o byte é o número da string (1 = a primeira); 0 = sem texto.</summary>
    private static string? Str(ReadOnlySpan<byte> s, List<string> strings, int field) =>
        field < s.Length && s[field] is > 0 and var i && i <= strings.Count ? strings[i - 1] : null;

    /// <summary>Tensão em mV (campos do SMBIOS 2.8+); null quando a estrutura é curta ou o valor é 0.</summary>
    private static int? Mv(ReadOnlySpan<byte> s, int field) =>
        s.Length >= field + 2 && BinaryPrimitives.ReadUInt16LittleEndian(s[field..]) is > 0 and var v ? v : null;

    private static List<string> Strings(ReadOnlySpan<byte> area)
    {
        var list = new List<string>();
        var start = 0;
        for (var i = 0; i <= area.Length; i++)
        {
            if (i < area.Length && area[i] != 0) continue;
            if (i > start) list.Add(Encoding.ASCII.GetString(area[start..i]));
            else if (i < area.Length) break; // dois zeros: fim
            start = i + 1;
        }
        return list;
    }

    private static string? Clean(string? s) =>
        s?.Trim() is { Length: > 0 } t && !t.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && !t.Equals("Undefined", StringComparison.OrdinalIgnoreCase)
        && !t.All(c => c == '0') ? t : null;

    public static IReadOnlyList<StorageDetails> ReadStorage()
    {
        var list = new List<StorageDetails>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            var (model, kind) = DiskOf(d.Name[..2]);
            list.Add(new StorageDetails(d.Name[..2], string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel, model, kind,
                d.TotalSize, d.AvailableFreeSpace, d.DriveFormat));
        }
        return list;
    }

    /// <summary>Modelo e tipo do disco de uma letra, pela consulta de propriedades de armazenamento (sem admin).</summary>
    private static (string? Model, string Kind) DiskOf(string drive)
    {
        using var h = CreateFileW($@"\\.\{drive}", 0, 3, 0, 3, 0, 0);
        if (h.IsInvalid) return (null, "Disco");

        string? model = null;
        int bus = 0;
        var query = stackalloc int[3]; // PropertyId, QueryType, AdditionalParameters
        query[0] = 0; query[1] = 0;     // StorageDeviceProperty, PropertyStandardQuery
        var buffer = stackalloc byte[1024];
        if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, buffer, 1024, out var returned, 0) && returned >= 36)
        {
            var span = new ReadOnlySpan<byte>(buffer, (int)returned);
            var vendorOffset = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
            var productOffset = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
            bus = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
            model = string.Join(" ", new[] { Ascii(span, vendorOffset), Ascii(span, productOffset) }.Where(x => !string.IsNullOrEmpty(x)));
            if (model.Length == 0) model = null;
        }

        bool? ssd = null;
        query[0] = 7; // StorageDeviceSeekPenaltyProperty
        if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, buffer, 1024, out returned, 0) && returned >= 9)
            ssd = buffer[8] == 0; // IncursSeekPenalty = false: SSD

        var kind = bus switch
        {
            17 => "SSD NVMe",
            7 => "USB",
            _ => ssd switch { true => "SSD SATA", false => "HD", null => "Disco" },
        };
        return (model, kind);
    }

    /// <summary>Texto ASCII terminado em zero a partir de um deslocamento do descritor.</summary>
    private static string? Ascii(ReadOnlySpan<byte> span, int offset) =>
        offset > 0 && offset < span.Length && span[offset..].IndexOf((byte)0) is var n and > 0 ? Encoding.ASCII.GetString(span.Slice(offset, n)).Trim() : null;

    public static OsDetails ReadOs()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var name = key?.GetValue("ProductName") as string ?? "Windows";
        var build = key?.GetValue("CurrentBuild") as string ?? "";
        var ubr = key?.GetValue("UBR") is int u ? u : 0;
        // O registro continua dizendo "Windows 10" no Windows 11; o build é que separa.
        if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
        return new OsDetails(name, key?.GetValue("DisplayVersion") as string ?? "", ubr > 0 ? $"{build}.{ubr}" : build);
    }

    /// <summary>Nome e versão do driver da placa de vídeo principal pelo registro (vale para qualquer marca).</summary>
    public static (string Name, string? Driver)? ReadGpuFromRegistry()
    {
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return null;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(sub);
            if (k?.GetValue("DriverDesc") is string name && !name.Contains("Basic", StringComparison.OrdinalIgnoreCase))
                return (name, k.GetValue("DriverVersion") as string);
        }
        return null;
    }

    private const int RelationProcessorCore = 0, RelationCache = 2, RelationAll = 0xFFFF;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetSystemFirmwareTable(uint provider, uint tableId, [Out] byte[]? buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, nint overlapped);
}
