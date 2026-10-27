using System.Net;
using Folga.Core.Logging;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Folga.Core.Network;

/// <summary>Bytes enviados e recebidos por PID desde o início do monitor.</summary>
public readonly record struct NetworkCounters(long Sent, long Received);

/// <summary>
/// Quem está usando a rede, por processo, pelo provider Microsoft-Windows-Kernel-Network (TCP e UDP,
/// IPv4 e IPv6). Gera um evento por envio/recebimento, então só fica ligado com a tela Rede aberta.
/// </summary>
public sealed class EtwNetworkMonitor : IDisposable
{
    private static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;
    public const string DefaultSessionName = "Folga-NetworkWatch";

    // IDs do manifesto: TCP v4/v6 = 10/26 enviado, 11/27 recebido; UDP v4/v6 = 42/58 enviado, 43/59 recebido.
    private static readonly HashSet<int> SentIds = [10, 26, 42, 58];
    private static readonly HashSet<int> ReceivedIds = [11, 27, 43, 59];

    private readonly object _gate = new();
    private readonly Dictionary<int, (long Sent, long Received)> _counters = [];
    private readonly Dictionary<IPAddress, long> _remoteBytes = [];
    private readonly string _sessionName;
    private readonly Func<int, bool>? _remoteFilter;
    private TraceEventSession? _session;
    private Thread? _thread;

    /// <param name="sessionName">Nome da sessão ETW; dois monitores ao mesmo tempo precisam de nomes diferentes.</param>
    /// <param name="remoteFilter">Para quais PIDs anotar o destino dos envios (ex.: o jogo, para achar o servidor).</param>
    public EtwNetworkMonitor(string sessionName = DefaultSessionName, Func<int, bool>? remoteFilter = null)
    {
        _sessionName = sessionName;
        _remoteFilter = remoteFilter;
    }

    /// <summary>Precisa de admin. Lança exceção se a sessão ETW não puder ser criada.</summary>
    public void Start()
    {
        if (TraceEventSession.IsElevated() != true) throw new UnauthorizedAccessException("precisa de administrador");
        _session = new TraceEventSession(_sessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
        _session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, KeywordIpv4 | KeywordIpv6);
        var parser = new RegisteredTraceEventParser(_session.Source);
        parser.All += OnEvent;
        _thread = new Thread(() =>
        {
            try { _session.Source.Process(); }
            catch (Exception ex) { Log.Error($"Monitor de rede parou: {ex.Message}"); }
        }) { IsBackground = true, Name = "Folga ETW rede" };
        _thread.Start();
    }

    public IReadOnlyDictionary<int, NetworkCounters> Snapshot()
    {
        lock (_gate) return _counters.ToDictionary(kv => kv.Key, kv => new NetworkCounters(kv.Value.Sent, kv.Value.Received));
    }

    /// <summary>Bytes enviados por endereço de destino, só dos PIDs do <c>remoteFilter</c>.</summary>
    public IReadOnlyDictionary<IPAddress, long> RemoteBytes()
    {
        lock (_gate) return new Dictionary<IPAddress, long>(_remoteBytes);
    }

    private void OnEvent(TraceEvent e)
    {
        var id = (int)e.ID;
        var sent = SentIds.Contains(id);
        if (!sent && !ReceivedIds.Contains(id)) return;
        // Pela mensagem do manifesto ("%2 bytes transmitted..."), o 1º campo é o PID e o 2º o tamanho.
        int pid;
        long size;
        try
        {
            pid = Convert.ToInt32(e.PayloadValue(0));
            size = Convert.ToInt64(e.PayloadValue(1));
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or IndexOutOfRangeException)
        {
            return;
        }
        var remote = sent && _remoteFilter?.Invoke(pid) == true ? ParseAddress(e.PayloadValue(2)) : null;
        lock (_gate)
        {
            _counters.TryGetValue(pid, out var c);
            _counters[pid] = sent ? (c.Sent + size, c.Received) : (c.Sent, c.Received + size);
            if (remote is not null) _remoteBytes[remote] = _remoteBytes.GetValueOrDefault(remote) + size;
        }
    }

    /// <summary>
    /// O endereço chega como o TraceEvent o decodifica: IPAddress/texto (quando o manifesto diz IPv4/IPv6),
    /// número de 32 bits na ordem de rede (IPv4 cru) ou 16 bytes (IPv6 cru).
    /// </summary>
    internal static IPAddress? ParseAddress(object? value) => value switch
    {
        IPAddress a => a,
        string s when IPAddress.TryParse(s, out var a) => a,
        byte[] { Length: 4 or 16 } b => new IPAddress(b),
        int i => new IPAddress((long)(uint)i),
        uint u => new IPAddress(u),
        long l when l is >= 0 and <= uint.MaxValue => new IPAddress(l),
        _ => null,
    };

    public void Dispose()
    {
        _session?.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _session = null;
    }
}

/// <summary>Transforma duas leituras cumulativas em taxa por app (soma dos processos de mesmo .exe).</summary>
public static class NetworkRates
{
    public sealed record AppRate(string ExeName, int Processes, double DownloadBytesPerSecond, double UploadBytesPerSecond);

    public static IReadOnlyList<AppRate> Compute(
        IReadOnlyDictionary<int, NetworkCounters> previous,
        IReadOnlyDictionary<int, NetworkCounters> current,
        TimeSpan elapsed,
        Func<int, string?> exeOf)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return current
            .Select(kv =>
            {
                var before = previous.GetValueOrDefault(kv.Key);
                return (Pid: kv.Key, Down: kv.Value.Received - before.Received, Up: kv.Value.Sent - before.Sent);
            })
            .Where(x => x.Down > 0 || x.Up > 0)
            .GroupBy(x => exeOf(x.Pid) ?? (x.Pid == 4 ? "System" : $"PID {x.Pid}"), StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppRate(g.Key, g.Count(), g.Sum(x => x.Down) / seconds, g.Sum(x => x.Up) / seconds))
            .OrderByDescending(a => a.DownloadBytesPerSecond + a.UploadBytesPerSecond)
            .ToList();
    }
}
