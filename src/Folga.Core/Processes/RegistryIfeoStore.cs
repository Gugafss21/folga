using Microsoft.Win32;

namespace Folga.Core.Processes;

/// <summary>HKLM\...\Image File Execution Options\&lt;exe&gt;\PerfOptions\CpuPriorityClass: o Windows aplica a prioridade ao criar o processo.</summary>
public sealed class RegistryIfeoStore : IIfeoStore
{
    private const string Root = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string ValueName = "CpuPriorityClass";

    public int? Get(string exeName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{Root}\{exeName}\PerfOptions");
        return key?.GetValue(ValueName) is int value ? value : null;
    }

    public void Set(string exeName, int? value)
    {
        if (value is int v)
        {
            using var key = Registry.LocalMachine.CreateSubKey($@"{Root}\{exeName}\PerfOptions", writable: true);
            key.SetValue(ValueName, v, RegistryValueKind.DWord);
            return;
        }

        using (var perf = Registry.LocalMachine.OpenSubKey($@"{Root}\{exeName}\PerfOptions", writable: true))
            perf?.DeleteValue(ValueName, throwOnMissingValue: false);
        DeleteIfEmpty($@"{Root}\{exeName}\PerfOptions");
        DeleteIfEmpty($@"{Root}\{exeName}");
    }

    private static void DeleteIfEmpty(string path)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(path))
        {
            if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
        }
        Registry.LocalMachine.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}
