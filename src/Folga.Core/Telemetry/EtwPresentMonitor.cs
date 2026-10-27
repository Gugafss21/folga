using Folga.Core.Logging;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Folga.Core.Telemetry;

/// <summary>
/// FPS e frametime do jogo pelos eventos de Present do DXGI (DirectX 10, 11 e 12), por fora do processo,
/// sem injetar nada. Só os PIDs do jogo entram na sessão (filtro do próprio ETW).
/// </summary>
public sealed class EtwPresentMonitor : IDisposable
{
    private static readonly Guid Dxgi = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
    private const ulong KeywordEvents = 0x2;
    private const int PresentStart = 42, PresentMultiplaneOverlayStart = 55;
    public const string DefaultSessionName = "Folga-PresentWatch";

    private readonly HashSet<int> _pids;
    private readonly string _sessionName;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, FrameStats> _bySwapChain = [];
    private TraceEventSession? _session;
    private Thread? _thread;
    private long _lastPresentTick;

    /// <param name="sessionName">Nome da sessão ETW; dois monitores ao mesmo tempo precisam de nomes diferentes.</param>
    public EtwPresentMonitor(IEnumerable<int> pids, string sessionName = DefaultSessionName)
    {
        _pids = [.. pids];
        _sessionName = sessionName;
    }

    public IReadOnlySet<int> Pids => _pids;

    /// <summary>Precisa de admin. Lança exceção se a sessão ETW não puder ser criada.</summary>
    public void Start()
    {
        if (TraceEventSession.IsElevated() != true) throw new UnauthorizedAccessException("precisa de administrador");
        _session = new TraceEventSession(_sessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
        _session.EnableProvider(Dxgi, TraceEventLevel.Informational, KeywordEvents,
            new TraceEventProviderOptions { ProcessIDFilter = _pids.ToList() });
        var parser = new RegisteredTraceEventParser(_session.Source);
        parser.All += OnEvent;
        _thread = new Thread(() =>
        {
            try { _session.Source.Process(); }
            catch (Exception ex) { Log.Error($"Monitor de FPS parou: {ex.Message}"); }
        }) { IsBackground = true, Name = "Folga ETW FPS" };
        _thread.Start();
        if (_sessionName == DefaultSessionName) Log.Info($"Monitor de FPS ligado (PID {string.Join(", ", _pids)})");
    }

    /// <summary>Milissegundos desde o último Present; long.MaxValue se nenhum ainda.</summary>
    public long MsSinceLastPresent
    {
        get
        {
            var last = Interlocked.Read(ref _lastPresentTick);
            return last == 0 ? long.MaxValue : Environment.TickCount64 - last;
        }
    }

    /// <summary>A cadeia com mais quadros no último segundo: é a do jogo (overlays e launchers apresentam pouco).</summary>
    public FrameStats? Main
    {
        get
        {
            lock (_gate)
                return _bySwapChain.Values.OrderByDescending(s => s.Recent(1_000).Count).ThenByDescending(s => s.Count).FirstOrDefault();
        }
    }

    /// <summary>A partida inteira da cadeia que mais desenhou (no fim, o jogo já pode ter parado de desenhar).</summary>
    public FrameSummary SessionSummary()
    {
        lock (_gate)
            return _bySwapChain.Values.Select(s => s.Session()).OrderByDescending(s => s.Frames).FirstOrDefault();
    }

    private void OnEvent(TraceEvent e)
    {
        var id = (int)e.ID;
        if (id is not (PresentStart or PresentMultiplaneOverlayStart) || !_pids.Contains(e.ProcessID)) return;

        ulong swapChain;
        try { swapChain = Convert.ToUInt64(e.PayloadValue(0)); }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or IndexOutOfRangeException) { swapChain = 0; }

        FrameStats stats;
        lock (_gate)
        {
            if (!_bySwapChain.TryGetValue(swapChain, out stats!)) _bySwapChain[swapChain] = stats = new FrameStats();
        }
        stats.AddPresent(e.TimeStampRelativeMSec);
        Interlocked.Exchange(ref _lastPresentTick, Environment.TickCount64);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _session = null;
    }
}

/// <summary>Medição curta de DirectX para a detecção automática: só enquanto o app é candidato a jogo.</summary>
public sealed class EtwPresentProbe : Detection.IPresentProbe
{
    private const long StaleAfterMs = 2_000;
    private readonly EtwPresentMonitor _monitor;

    private EtwPresentProbe(EtwPresentMonitor monitor) => _monitor = monitor;

    /// <summary>null sem admin ou se a sessão ETW não abrir; a detecção segue só com a tela cheia.</summary>
    public static EtwPresentProbe? TryStart(int pid)
    {
        if (TraceEventSession.IsElevated() != true) return null;
        var monitor = new EtwPresentMonitor([pid], "Folga-GameProbe");
        try
        {
            monitor.Start();
            return new EtwPresentProbe(monitor);
        }
        catch (Exception ex)
        {
            monitor.Dispose();
            Log.Warn($"Medição de DirectX para detecção indisponível: {ex.Message}");
            return null;
        }
    }

    public double Fps => _monitor.MsSinceLastPresent > StaleAfterMs ? 0 : _monitor.Main?.Fps() ?? 0;

    public void Dispose() => _monitor.Dispose();
}
