using System.Diagnostics;
using Folga.Core.Native;
using static Folga.Core.Native.Kernel32;

namespace Folga.Core.Telemetry;

/// <summary>Estado do PC a cada segundo de jogo, para o diagnóstico das travadas.</summary>
public interface ISystemProbe : IDisposable
{
    double TotalRamMb { get; }

    /// <summary>As taxas são desde a chamada anterior (na primeira, ficam null). A VRAM vem da leitura da GPU.</summary>
    SystemSample Read(double? vramPercent);
}

/// <summary>
/// Leitura barata, sem ETW: bytes lidos e tempo de CPU dos processos do jogo (GetProcessIoCounters e
/// GetProcessTimes), CPU do sistema (GetSystemTimes), páginas buscadas no disco (contador "Pages Input/sec")
/// e RAM disponível.
/// </summary>
public sealed class WindowsSystemProbe : ISystemProbe
{
    private const double Mb = 1024 * 1024;

    private readonly List<ProcessHandle> _games = [];
    private readonly PdhCounterArray _pagesInput = new(@"\Memory\Pages Input/sec");
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSeconds;
    private (ulong Read, long Cpu)? _lastGame;
    private (long Idle, long Busy, long Total)? _lastSystem;

    public WindowsSystemProbe(IEnumerable<int> gamePids)
    {
        foreach (var pid in gamePids)
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h.IsInvalid) h.Dispose();
            else _games.Add(h);
        }
        TotalRamMb = Memory().ullTotalPhys / Mb;
        _ = Read(null); // primeira leitura: só a base para as taxas
    }

    public double TotalRamMb { get; }

    public SystemSample Read(double? vramPercent)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = now - _lastSeconds;
        _lastSeconds = now;

        var game = ReadGame();
        double? diskMBps = null, gameCpuShare = null;
        if (game is { } g && _lastGame is { } lg && elapsed > 0 && g.Read >= lg.Read)
            diskMBps = (g.Read - lg.Read) / Mb / elapsed;

        double? cpu = null, other = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var system = (Idle: idle, Busy: kernel + user - idle, Total: kernel + user);
            if (_lastSystem is { } ls && system.Total > ls.Total)
            {
                var total = (double)(system.Total - ls.Total);
                cpu = Math.Clamp((system.Busy - ls.Busy) / total * 100, 0, 100);
                if (game is { } gc && _lastGame is { } lgc) gameCpuShare = Math.Clamp((gc.Cpu - lgc.Cpu) / total * 100, 0, 100);
                if (gameCpuShare is { } share) other = Math.Max(0, cpu.Value - share);
            }
            _lastSystem = system;
        }
        _lastGame = game;

        // O contador já é uma taxa por segundo (média desde a leitura anterior); página = 4 KB.
        double? faults = _pagesInput.Read() is [var (_, pages), ..] ? pages * Environment.SystemPageSize / Mb : null;

        return new SystemSample(diskMBps, faults, Memory().ullAvailPhys / Mb, vramPercent, cpu, other, AfterLoading: false);
    }

    /// <summary>Soma de todos os processos do jogo; null se nenhum pôde ser lido (protegido ou já fechou).</summary>
    private (ulong Read, long Cpu)? ReadGame()
    {
        ulong read = 0;
        long cpu = 0;
        var any = false;
        foreach (var h in _games)
        {
            if (!GetProcessIoCounters(h, out var io) || !GetProcessTimes(h, out _, out _, out var k, out var u)) continue;
            read += io.ReadTransferCount;
            cpu += k + u;
            any = true;
        }
        return any ? (read, cpu) : null;
    }

    private static unsafe MEMORYSTATUSEX Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        GlobalMemoryStatusEx(ref status);
        return status;
    }

    public void Dispose()
    {
        foreach (var h in _games) h.Dispose();
        _games.Clear();
        _pagesInput.Dispose();
    }
}
