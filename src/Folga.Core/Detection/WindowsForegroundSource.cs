using System.Diagnostics;
using Folga.Core.Native;
using Folga.Core.Processes;
using static Folga.Core.Native.User32;

namespace Folga.Core.Detection;

/// <summary>Lê a janela em primeiro plano pelo user32: dono, título e se está em tela cheia.</summary>
public sealed unsafe class WindowsForegroundSource : IForegroundSource
{
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    public ForegroundWindow? Read()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || hwnd == GetShellWindow() || IsIconic(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;

        var path = WindowsProcessControl.ImagePath((int)pid);
        var exe = path is not null ? Path.GetFileName(path) : ExeFromProcess((int)pid);
        if (exe is null) return null;

        return new ForegroundWindow((int)pid, exe, Title(hwnd), IsFullscreen(hwnd),
            path?.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsFullscreen(nint hwnd)
    {
        // Sem barra de título: descarta janelas maximizadas comuns (com a barra de tarefas oculta elas também cobrem o monitor).
        if ((GetWindowLongPtr(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false;

        // Coordenadas reais de pixel, sem a virtualização de DPI, para comparar com o monitor.
        var previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            if (!GetWindowRect(hwnd, out var window)) return false;
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return false;
            var m = info.rcMonitor;
            return window.Left <= m.Left && window.Top <= m.Top && window.Right >= m.Right && window.Bottom >= m.Bottom;
        }
        finally
        {
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    private static string Title(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetWindowText(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    // Processo protegido que nega até a consulta limitada: o nome ainda vem pela lista de processos.
    private static string? ExeFromProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
