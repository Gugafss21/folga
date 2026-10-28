using System.Runtime.InteropServices;
using System.Text;
using Folga.Core.Logging;
using Folga.Core.Native;

namespace Folga.Core.Telemetry;

/// <summary>Leitura da GPU. Valores null = a fonte não informa (ex.: temperatura fora da NVIDIA).</summary>
public sealed record GpuSnapshot(string Name, double? UsagePercent, long? VramUsedBytes, long? VramTotalBytes, int? TemperatureC, string Source);

/// <summary>
/// Ficha da placa NVIDIA pela NVML: clocks agora e máximos, limite de energia padrão (de fábrica) e o que está
/// valendo, e o link PCIe agora e o máximo. null onde o driver não informa.
/// </summary>
public sealed record GpuDetails(string Name, string? Driver, long? VramTotalBytes,
    int? CoreMhz, int? CoreMaxMhz, int? MemoryMhz, int? MemoryMaxMhz,
    int? PowerDefaultW, int? PowerLimitW, int? PowerNowW,
    int? PcieGen, int? PcieMaxGen, int? PcieWidth, int? PcieMaxWidth, int? TemperatureC);

/// <summary>
/// NVIDIA: NVML (nvml.dll vem com o driver; uso, VRAM e temperatura, sem driver extra).
/// Outras placas: contadores do Windows (GPU Engine e GPU Adapter Memory; sem temperatura).
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    private readonly NvmlGpu? _nvml;
    private readonly PdhCounterArray _engine = new(@"\GPU Engine(*)\Utilization Percentage");
    private readonly PdhCounterArray _memory = new(@"\GPU Adapter Memory(*)\Dedicated Usage");

    public GpuMonitor()
    {
        _nvml = NvmlGpu.TryOpen();
        Log.Info(_nvml is not null ? $"GPU: {_nvml.Name} (NVML)" : "GPU: contadores do Windows (sem NVML)");
    }

    public GpuSnapshot? Read()
    {
        if (_nvml?.Read() is { } nv) return nv;

        var usage = _engine.Read() is { } engines ? WindowsUsage(engines) : null;
        var vram = _memory.Read() is { Count: > 0 } mem ? (long)mem.Max(m => m.Value) : (long?)null;
        return usage is null && vram is null ? null : new GpuSnapshot("GPU", usage, vram, null, null, "Windows");
    }

    /// <summary>Ficha detalhada (só NVIDIA, pela NVML); null nas outras placas.</summary>
    public GpuDetails? Details() => _nvml?.Details();

    /// <summary>
    /// Uso 3D por placa: soma das instâncias "engtype_3D" de cada adaptador (luid), igual ao Gerenciador de
    /// Tarefas; com mais de uma placa, vale a mais usada.
    /// </summary>
    internal static double? WindowsUsage(IReadOnlyList<(string Instance, double Value)> engines)
    {
        var byAdapter = engines
            .Where(e => e.Instance.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => AdapterOf(e.Instance))
            .Select(g => g.Sum(e => e.Value))
            .ToList();
        return byAdapter.Count == 0 ? null : Math.Clamp(byAdapter.Max(), 0, 100);
    }

    private static string AdapterOf(string instance)
    {
        // pid_1234_luid_0x00000000_0x0000ff81_phys_0_eng_0_engtype_3D → luid_0x00000000_0x0000ff81
        var start = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        var end = instance.IndexOf("_phys", StringComparison.OrdinalIgnoreCase);
        return start >= 0 && end > start ? instance[start..end] : instance;
    }

    public void Dispose()
    {
        _nvml?.Dispose();
        _engine.Dispose();
        _memory.Dispose();
    }
}

internal sealed unsafe partial class NvmlGpu : IDisposable
{
    private const int NVML_SUCCESS = 0;
    private const int NVML_TEMPERATURE_GPU = 0;

    private readonly nint _device;
    public string Name { get; }

    private NvmlGpu(nint device, string name)
    {
        _device = device;
        Name = name;
    }

    /// <summary>null se não há NVIDIA, se o driver não tem NVML ou se a inicialização falha.</summary>
    public static NvmlGpu? TryOpen()
    {
        try
        {
            if (nvmlInit_v2() != NVML_SUCCESS) return null;
            if (nvmlDeviceGetHandleByIndex_v2(0, out var device) != NVML_SUCCESS)
            {
                nvmlShutdown();
                return null;
            }
            var buffer = stackalloc byte[96];
            var text = "NVIDIA";
            if (nvmlDeviceGetName(device, buffer, 96) == NVML_SUCCESS)
            {
                var span = new ReadOnlySpan<byte>(buffer, 96);
                var length = span.IndexOf((byte)0);
                text = Encoding.UTF8.GetString(length >= 0 ? span[..length] : span);
            }
            return new NvmlGpu(device, text);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    public GpuSnapshot? Read()
    {
        double? usage = nvmlDeviceGetUtilizationRates(_device, out var u) == NVML_SUCCESS ? u.Gpu : null;
        int? temperature = nvmlDeviceGetTemperature(_device, NVML_TEMPERATURE_GPU, out var t) == NVML_SUCCESS ? (int)t : null;
        var hasMemory = nvmlDeviceGetMemoryInfo(_device, out var m) == NVML_SUCCESS;
        if (usage is null && temperature is null && !hasMemory) return null;
        return new GpuSnapshot(Name, usage, hasMemory ? (long)m.Used : null, hasMemory ? (long)m.Total : null, temperature, "NVML");
    }

    public GpuDetails Details()
    {
        int? Clock(bool max, int type) =>
            (max ? nvmlDeviceGetMaxClockInfo(_device, type, out var v) : nvmlDeviceGetClockInfo(_device, type, out v)) == NVML_SUCCESS && v > 0 ? (int)v : null;
        int? Watts(int result, uint mw) => result == NVML_SUCCESS && mw > 0 ? (int)Math.Round(mw / 1000.0) : null;
        int? Value(int result, uint v) => result == NVML_SUCCESS ? (int)v : null;

        string? driver = null;
        var buffer = stackalloc byte[96];
        if (nvmlSystemGetDriverVersion(buffer, 96) == NVML_SUCCESS)
        {
            var span = new ReadOnlySpan<byte>(buffer, 96);
            var length = span.IndexOf((byte)0);
            driver = Encoding.ASCII.GetString(length >= 0 ? span[..length] : span);
        }
        var memory = nvmlDeviceGetMemoryInfo(_device, out var m) == NVML_SUCCESS ? (long)m.Total : (long?)null;
        return new GpuDetails(Name, driver, memory,
            Clock(false, ClockGraphics), Clock(true, ClockGraphics), Clock(false, ClockMemory), Clock(true, ClockMemory),
            Watts(nvmlDeviceGetPowerManagementDefaultLimit(_device, out var pd), pd), Watts(nvmlDeviceGetEnforcedPowerLimit(_device, out var pl), pl),
            Watts(nvmlDeviceGetPowerUsage(_device, out var pu), pu),
            Value(nvmlDeviceGetCurrPcieLinkGeneration(_device, out var g), g), Value(nvmlDeviceGetMaxPcieLinkGeneration(_device, out var gm), gm),
            Value(nvmlDeviceGetCurrPcieLinkWidth(_device, out var w), w), Value(nvmlDeviceGetMaxPcieLinkWidth(_device, out var wm), wm),
            Value(nvmlDeviceGetTemperature(_device, NVML_TEMPERATURE_GPU, out var t), t));
    }

    private const int ClockGraphics = 0, ClockMemory = 2;

    public void Dispose()
    {
        try { nvmlShutdown(); } catch (DllNotFoundException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization { public uint Gpu, Memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory { public ulong Total, Free, Used; }

    [LibraryImport("nvml.dll")] private static partial int nvmlInit_v2();
    [LibraryImport("nvml.dll")] private static partial int nvmlShutdown();
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetName(nint device, byte* name, uint length);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetUtilizationRates(nint device, out NvmlUtilization utilization);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetTemperature(nint device, int sensor, out uint temperature);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetMemoryInfo(nint device, out NvmlMemory memory);
    [LibraryImport("nvml.dll")] private static partial int nvmlSystemGetDriverVersion(byte* version, uint length);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetClockInfo(nint device, int type, out uint mhz);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetMaxClockInfo(nint device, int type, out uint mhz);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetPowerManagementDefaultLimit(nint device, out uint milliwatts);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetEnforcedPowerLimit(nint device, out uint milliwatts);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetPowerUsage(nint device, out uint milliwatts);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetCurrPcieLinkGeneration(nint device, out uint generation);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetMaxPcieLinkGeneration(nint device, out uint generation);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetCurrPcieLinkWidth(nint device, out uint width);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetMaxPcieLinkWidth(nint device, out uint width);
}
