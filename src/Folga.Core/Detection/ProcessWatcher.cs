using System.Diagnostics;
using Folga.Core.Logging;
using Folga.Core.Processes;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Folga.Core.Detection;

public interface IProcessWatcher : IDisposable
{
    event Action<int, string>? Started;
    event Action<int>? Stopped;
    void Start();
}

public static class ProcessWatcherFactory
{
    /// <summary>ETW quando possível (milissegundos, precisa de admin); senão, varredura periódica.</summary>
    public static IProcessWatcher Create(IProcessControl control)
    {
        if (TraceEventSession.IsElevated() == true)
        {
            var etw = new EtwProcessWatcher();
            try
            {
                etw.Start();
                return new StartedWatcher(etw);
            }
            catch (Exception ex)
            {
                etw.Dispose();
                Log.Warn($"ETW indisponível ({ex.Message}); usando varredura a cada 2 s");
            }
        }
        return new PollingProcessWatcher(control, TimeSpan.FromSeconds(2));
    }

    // O ETW já foi iniciado na fábrica para detectar falha cedo; Start() do chamador vira no-op.
    private sealed class StartedWatcher(IProcessWatcher inner) : IProcessWatcher
    {
        public event Action<int, string>? Started { add => inner.Started += value; remove => inner.Started -= value; }
        public event Action<int>? Stopped { add => inner.Stopped += value; remove => inner.Stopped -= value; }
        public void Start() { }
        public void Dispose() => inner.Dispose();
    }
}

/// <summary>Eventos de início/fim de processo do provider Microsoft-Windows-Kernel-Process.</summary>
public sealed class EtwProcessWatcher : IProcessWatcher
{
    private static readonly Guid KernelProcess = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    private const ulong KeywordProcess = 0x10;
    private const string SessionName = "Folga-ProcessWatch";

    private TraceEventSession? _session;
    private Thread? _thread;

    public event Action<int, string>? Started;
    public event Action<int>? Stopped;

    public void Start()
    {
        // Create fecha uma sessão de mesmo nome que tenha sobrado de um crash.
        _session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
        _session.EnableProvider(KernelProcess, TraceEventLevel.Informational, KeywordProcess);
        var parser = new RegisteredTraceEventParser(_session.Source);
        parser.All += OnEvent;
        _thread = new Thread(() =>
        {
            try { _session.Source.Process(); }
            catch (Exception ex) { Log.Error($"ETW parou: {ex.Message}"); }
        }) { IsBackground = true, Name = "Folga ETW" };
        _thread.Start();
    }

    private void OnEvent(TraceEvent e)
    {
        if (e.ProviderGuid != KernelProcess) return;
        try
        {
            switch ((int)e.ID)
            {
                case 1: // ProcessStart
                    var pid = Convert.ToInt32(e.PayloadByName("ProcessID"));
                    var image = e.PayloadByName("ImageName") as string;
                    if (!string.IsNullOrEmpty(image)) Started?.Invoke(pid, Path.GetFileName(image));
                    break;
                case 2: // ProcessStop
                    Stopped?.Invoke(Convert.ToInt32(e.PayloadByName("ProcessID")));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Erro tratando evento de processo: {ex}");
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}

/// <summary>Plano B sem ETW: compara a lista de processos a cada intervalo.</summary>
public sealed class PollingProcessWatcher(IProcessControl control, TimeSpan interval) : IProcessWatcher
{
    private readonly Dictionary<int, string> _known = [];
    private Timer? _timer;
    private int _busy;

    public event Action<int, string>? Started;
    public event Action<int>? Stopped;

    public void Start()
    {
        foreach (var p in control.Snapshot()) _known[p.Pid] = p.ExeName;
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = control.Snapshot().ToDictionary(p => p.Pid, p => p.ExeName);
            foreach (var pid in _known.Keys.Where(pid => !now.ContainsKey(pid)).ToList())
            {
                _known.Remove(pid);
                Stopped?.Invoke(pid);
            }
            foreach (var (pid, exe) in now)
            {
                if (_known.TryAdd(pid, exe)) Started?.Invoke(pid, exe);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Erro na varredura de processos: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
