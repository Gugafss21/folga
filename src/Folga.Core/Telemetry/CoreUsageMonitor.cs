using Folga.Core.Logging;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Folga.Core.Telemetry;

/// <summary>Uso de uma thread lógica num intervalo: quanto foi do jogo e quanto de outros programas (0 a 100).</summary>
public readonly record struct CoreShare(double GamePercent, double OtherPercent);

/// <summary>
/// Conta as amostras do kernel por thread lógica: a cada amostra, quem estava rodando ali (o jogo, outro
/// programa ou ninguém). A parte de cada um no total de amostras é o uso naquele intervalo.
/// </summary>
public sealed class CoreUsageCounter
{
    private readonly object _gate = new();
    private readonly long[] _total, _game, _idle;
    private readonly HashSet<int> _gamePids;

    public CoreUsageCounter(int processors, IEnumerable<int> gamePids)
    {
        _total = new long[processors];
        _game = new long[processors];
        _idle = new long[processors];
        _gamePids = [.. gamePids];
    }

    public void Add(int processor, int pid)
    {
        if ((uint)processor >= (uint)_total.Length) return;
        lock (_gate)
        {
            _total[processor]++;
            if (pid == 0) _idle[processor]++; // processo ocioso do Windows
            else if (_gamePids.Contains(pid)) _game[processor]++;
        }
    }

    /// <summary>Uso de cada thread lógica desde a última chamada (null onde não houve amostra) e zera a contagem.</summary>
    public CoreShare?[] Take()
    {
        lock (_gate)
        {
            var result = new CoreShare?[_total.Length];
            for (var i = 0; i < _total.Length; i++)
            {
                if (_total[i] == 0) continue;
                double total = _total[i];
                result[i] = new CoreShare(100 * _game[i] / total, 100 * (_total[i] - _game[i] - _idle[i]) / total);
            }
            Array.Clear(_total);
            Array.Clear(_game);
            Array.Clear(_idle);
            return result;
        }
    }
}

/// <summary>
/// Quanto de cada thread lógica o jogo está usando de verdade, pela amostragem de CPU do kernel (o mesmo
/// princípio dos profilers: cerca de mil amostras por segundo em cada thread). Precisa de administrador.
/// Ligado só enquanto a tela Agora está à vista com um jogo aberto.
/// </summary>
public sealed class EtwCoreUsageMonitor : IDisposable
{
    private const string SessionName = "Folga-CoreUsage";

    private readonly CoreUsageCounter _counter;
    private TraceEventSession? _session;
    private Thread? _thread;

    public EtwCoreUsageMonitor(IEnumerable<int> gamePids) =>
        _counter = new CoreUsageCounter(Environment.ProcessorCount, gamePids);

    /// <summary>Lança exceção sem administrador ou se a sessão do kernel não abrir.</summary>
    public void Start()
    {
        if (TraceEventSession.IsElevated() != true) throw new UnauthorizedAccessException("precisa de administrador");
        // Sessão própria do kernel (Windows 8 ou mais novo): não disputa a "NT Kernel Logger" com outras ferramentas.
        _session = new TraceEventSession(SessionName) { StopOnDispose = true };
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Profile | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);
        _session.Source.Kernel.PerfInfoSample += e => _counter.Add(e.ProcessorNumber, e.ProcessID);
        _thread = new Thread(() =>
        {
            try { _session.Source.Process(); }
            catch (Exception ex) { Log.Error($"Uso dos núcleos parou: {ex.Message}"); }
        }) { IsBackground = true, Name = "Folga ETW núcleos" };
        _thread.Start();
    }

    /// <summary>Índice = número do processador lógico (o mesmo LogicalIndex do CPU Set no grupo 0).</summary>
    public CoreShare?[] Take() => _counter.Take();

    public void Dispose()
    {
        _session?.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _session = null;
    }
}
