using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Folga.Core.Logging;
using Folga.Core.Network;

namespace Folga.Core.Telemetry;

public sealed record PingState(int? RttMs, IPAddress? Server, string Status);

/// <summary>
/// Ping do servidor do jogo. O servidor é descoberto pelo próprio tráfego do jogo (para onde ele mais
/// envia dados), em rajadas curtas de ETW a cada minuto, para não ficar contando pacote o jogo inteiro.
/// O ping é ICMP; servidor que bloqueia ICMP aparece como "sem resposta".
/// </summary>
public sealed class PingMonitor : IDisposable
{
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RediscoverEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(2);
    private const int PingTimeoutMs = 1000;

    private readonly HashSet<int> _pids;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private volatile PingState _state = new(null, null, "procurando o servidor do jogo");

    public PingMonitor(IEnumerable<int> pids)
    {
        _pids = [.. pids];
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public PingState State => _state;

    private async Task RunAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        var lastDiscovery = DateTime.MinValue;
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_state.Server is null || DateTime.UtcNow - lastDiscovery > RediscoverEvery)
                {
                    lastDiscovery = DateTime.UtcNow;
                    var server = await DiscoverAsync(ct);
                    if (server is not null && !server.Equals(_state.Server))
                    {
                        Log.Info($"Servidor do jogo: {server}");
                        failures = 0;
                    }
                    if (server is null && _state.Server is null)
                    {
                        _state = _state with { Status = "servidor do jogo não identificado (ainda no menu?)" };
                        await Task.Delay(PingEvery, ct);
                        continue;
                    }
                    if (server is not null) _state = _state with { Server = server };
                }

                var target = _state.Server!;
                try
                {
                    var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(PingTimeoutMs), cancellationToken: ct);
                    if (reply.Status == IPStatus.Success)
                    {
                        failures = 0;
                        _state = new PingState((int)reply.RoundtripTime, target, $"ICMP para {target}");
                    }
                    else if (++failures >= 3)
                    {
                        _state = new PingState(null, target, $"{target} não responde a ping (o servidor bloqueia ICMP)");
                    }
                }
                catch (PingException)
                {
                    if (++failures >= 3) _state = new PingState(null, target, "sem rota para o servidor");
                }
                await Task.Delay(PingEvery, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<IPAddress?> DiscoverAsync(CancellationToken ct)
    {
        try
        {
            using var monitor = new EtwNetworkMonitor("Folga-PingWatch", pid => _pids.Contains(pid));
            monitor.Start();
            await Task.Delay(DiscoveryWindow, ct);
            return ServerPicker.Pick(monitor.RemoteBytes());
        }
        catch (UnauthorizedAccessException)
        {
            _state = new PingState(null, null, "ping precisa do Folga como administrador");
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(7)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}

public static class ServerPicker
{
    private const long MinimumBytes = 4 * 1024; // menos que isso em 5 s é chat, telemetria, nada de partida

    /// <summary>O endereço público para onde o jogo mais enviou dados. Rede local, loopback e multicast ficam de fora.</summary>
    public static IPAddress? Pick(IReadOnlyDictionary<IPAddress, long> bytesByAddress) =>
        bytesByAddress
            .Where(kv => kv.Value >= MinimumBytes && IsPublic(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .FirstOrDefault();

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127) // CGNAT do provedor
                || b[0] == 0 || b[0] >= 224);                 // multicast e reservados
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
        }
        return false;
    }
}
