using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Folga.Core.Native;

// P/Invoke escrito à mão: boa parte do que usamos (ntdll) não está nos metadados do CsWin32.

internal sealed class ProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ProcessHandle() : base(true) { }
    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}

internal static unsafe partial class Kernel32
{
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    public const int ProcessMemoryPriority = 0;
    public const int ProcessPowerThrottling = 4;

    public const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1;
    public const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2;
    public const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x4;
    public const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial ProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetPriorityClass(ProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetPriorityClass(ProcessHandle process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(ProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemCpuSetInformation(byte* buffer, uint length, out uint returned, nint process, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDefaultCpuSets(ProcessHandle process, uint* ids, uint count);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessDefaultCpuSets(ProcessHandle process, uint* ids, uint count, out uint required);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(ProcessHandle process, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessInformation(ProcessHandle process, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSizeEx(ProcessHandle process, nint min, nint max, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessWorkingSetSizeEx(ProcessHandle process, out nint min, out nint max, out uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageNameW(ProcessHandle process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool K32EmptyWorkingSet(ProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool K32GetProcessMemoryInfo(ProcessHandle process, void* counters, uint cb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}

internal static unsafe partial class Ntdll
{
    public const int ProcessIoPriority = 33;
    public const int SystemMemoryListInformation = 80;
    public const int MemoryPurgeStandbyList = 4;

    [LibraryImport("ntdll.dll")]
    public static partial int NtSetInformationProcess(ProcessHandle process, int infoClass, void* info, uint length);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(ProcessHandle process, int infoClass, void* info, uint length, out uint returned);

    [LibraryImport("ntdll.dll")]
    public static partial int NtSetSystemInformation(int infoClass, void* info, uint length);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int infoClass, void* info, uint length, out uint returned);

    [LibraryImport("ntdll.dll")]
    public static partial int NtSuspendProcess(ProcessHandle process);

    [LibraryImport("ntdll.dll")]
    public static partial int NtResumeProcess(ProcessHandle process);
}

internal static partial class Advapi32
{
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint SE_PRIVILEGE_ENABLED = 0x2;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TOKEN_PRIVILEGES newState, uint length, nint previous, nint returnLength);
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public LUID Luid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_POWER_THROTTLING_STATE
{
    public uint Version;
    public uint ControlMask;
    public uint StateMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_MEMORY_COUNTERS_EX2
{
    public uint cb;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivateUsage;
    public nuint PrivateWorkingSetSize;
    public ulong SharedCommitUsage;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SYSTEM_MEMORY_LIST_INFORMATION
{
    public nuint ZeroPageCount;
    public nuint FreePageCount;
    public nuint ModifiedPageCount;
    public nuint ModifiedNoWritePageCount;
    public nuint BadPageCount;
    public fixed ulong PageCountByPriority[8];
    public fixed ulong RepurposedPagesByPriority[8];
    public nuint ModifiedPageCountPageFile;
}
