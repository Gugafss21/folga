using Folga.Core.Logging;
using Folga.Core.Profiles;

namespace Folga.Core.Network;

/// <summary>Política de QoS do Windows para um .exe: limite de upload e/ou marcação DSCP.</summary>
public sealed record QosPolicy(string Name, string Exe, long? ThrottleBitsPerSecond, int? Dscp);

/// <summary>O que a sessão de jogo muda na rede.</summary>
public sealed record NetworkPlan(IReadOnlyList<QosPolicy> Policies, bool PauseWindowsUpdate)
{
    public bool IsEmpty => Policies.Count == 0 && !PauseWindowsUpdate;
}

public interface INetworkControl : IDisposable
{
    /// <summary>Aplica o plano em segundo plano (chamadas lentas: PowerShell, serviços). Retorna na hora.</summary>
    void Begin(NetworkPlan plan);

    /// <summary>Desfaz tudo o que está no diário com tipo de rede, inclusive sobras de um crash. Retorna na hora.</summary>
    void End();
}

public static class NetworkPlanner
{
    public const string PolicyPrefix = "Folga-";
    public const int ExpeditedForwarding = 46; // DSCP EF: o mesmo de voz sobre IP

    /// <param name="gameExes">.exe reais dos jogos abertos (para a marcação DSCP).</param>
    public static NetworkPlan Build(FolgaConfig config, IEnumerable<(GameProfile Profile, string Exe)> gameExes)
    {
        var policies = new Dictionary<string, QosPolicy>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in config.Background.Where(r => r.Enabled && r.UploadLimitKbps > 0))
        {
            foreach (var exe in rule.Executables)
            {
                // A política de QoS casa pelo nome exato do .exe; padrões com * não têm equivalente.
                if (exe.Contains('*') || exe.Contains('?'))
                {
                    Log.Warn($"{rule.Name}: limite de upload ignorado para '{exe}' (a QoS do Windows não aceita *)");
                    continue;
                }
                policies.TryAdd(exe, new QosPolicy($"{PolicyPrefix}up-{exe}", exe, rule.UploadLimitKbps * 1000L, null));
            }
        }

        foreach (var (profile, exe) in gameExes.Where(g => g.Profile.MarkDscp))
            policies[exe] = new QosPolicy($"{PolicyPrefix}dscp-{exe}", exe, null, ExpeditedForwarding);

        return new NetworkPlan(policies.Values.ToList(), config.Network.PauseWindowsUpdate);
    }
}
