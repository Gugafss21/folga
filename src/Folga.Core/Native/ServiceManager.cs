using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Folga.Core.Native;

/// <summary>Parar/iniciar um serviço pelo Service Control Manager, sem carregar o ServiceController.</summary>
internal static partial class ServiceManager
{
    public enum State { Unknown = 0, Stopped = 1, StartPending = 2, StopPending = 3, Running = 4 }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_CONTROL_STOP = 0x1;
    private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    public static State GetState(string name)
    {
        var service = Open(name, SERVICE_QUERY_STATUS, out _);
        if (service == 0) return State.Unknown;
        try { return QueryServiceStatus(service, out var s) ? (State)s.CurrentState : State.Unknown; }
        finally { CloseServiceHandle(service); }
    }

    public static bool Stop(string name, out string? error)
    {
        var service = Open(name, SERVICE_STOP | SERVICE_QUERY_STATUS, out error);
        if (service == 0) return false;
        try
        {
            if (ControlService(service, SERVICE_CONTROL_STOP, out _) || Marshal.GetLastPInvokeError() == ERROR_SERVICE_NOT_ACTIVE) return true;
            error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            return false;
        }
        finally { CloseServiceHandle(service); }
    }

    public static bool Start(string name, out string? error)
    {
        var service = Open(name, SERVICE_START, out error);
        if (service == 0) return false;
        try
        {
            if (StartServiceW(service, 0, 0) || Marshal.GetLastPInvokeError() == ERROR_SERVICE_ALREADY_RUNNING) return true;
            error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            return false;
        }
        finally { CloseServiceHandle(service); }
    }

    private static nint Open(string name, uint access, out string? error)
    {
        error = null;
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == 0) { error = new Win32Exception(Marshal.GetLastPInvokeError()).Message; return 0; }
        try
        {
            var service = OpenServiceW(scm, name, access);
            if (service == 0) error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            return service;
        }
        finally { CloseServiceHandle(scm); }
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenServiceW(nint scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(nint service, out SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(nint service, uint control, out SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(nint service, uint argc, nint argv);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
