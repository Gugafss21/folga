using System.IO.Enumeration;

namespace Folga.Core.Profiles;

public enum PriorityLevel { Idle, BelowNormal, Normal, AboveNormal, High }

/// <summary>Onde ficam os apps de fundo durante o jogo.</summary>
public enum BackgroundCores
{
    /// <summary>Sem restrição de núcleos.</summary>
    All,
    /// <summary>Núcleos E em CPU híbrida; senão, o último núcleo.</summary>
    Auto,
    LastCore,
    LastTwoCores,
}

public enum GameCores
{
    /// <summary>O jogo pode usar todos os núcleos (padrão).</summary>
    All,
    /// <summary>O jogo fica só com os núcleos que não foram dados ao fundo.</summary>
    ExcludeBackground,
}

public sealed class GameProfile
{
    public string Name { get; set; } = "";
    public List<string> Executables { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public PriorityLevel Priority { get; set; } = PriorityLevel.High;
    public GameCores Cores { get; set; } = GameCores.All;

    /// <summary>Se o anti-cheat negar acesso, grava a prioridade no IFEO (vale na próxima abertura).</summary>
    public bool UseIfeoFallback { get; set; } = true;

    /// <summary>Marca o tráfego do jogo com DSCP 46 (prioridade de voz). Só ajuda se o roteador respeitar QoS.</summary>
    public bool MarkDscp { get; set; }

    /// <summary>Perfil criado pela detecção automática (tela cheia + DirectX), não à mão.</summary>
    public bool AutoDetected { get; set; }

    public bool Matches(string exe) => Enabled && ExeMatcher.MatchesAny(Executables, exe);
}

public sealed class BackgroundRule
{
    public string Name { get; set; } = "";
    public List<string> Executables { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public PriorityLevel? Priority { get; set; } = PriorityLevel.BelowNormal;
    public BackgroundCores Cores { get; set; } = BackgroundCores.Auto;
    public bool EcoQos { get; set; } = true;
    public bool LowIoPriority { get; set; } = true;
    public bool LowMemoryPriority { get; set; } = true;

    /// <summary>Orçamento de RAM somando todos os processos do app; acima dele os maiores são esvaziados. 0 = sem orçamento.</summary>
    public int MemoryBudgetMb { get; set; }

    /// <summary>Teto rígido de working set por processo (pagina para o disco, não derruba). 0 = desligado.</summary>
    public int HardWorkingSetCapMb { get; set; }

    /// <summary>Limite de upload do app durante o jogo, em kbit/s (política de QoS do Windows). 0 = sem limite.</summary>
    public int UploadLimitKbps { get; set; }

    /// <summary>Congela o app inteiro durante o jogo (para CPU, disco e rede). Agressivo: desligado por padrão.</summary>
    public bool SuspendDuringGame { get; set; }

    public bool Matches(string exe) => Enabled && ExeMatcher.MatchesAny(Executables, exe);
}

public sealed class MemorySettings
{
    public bool PurgeStandbyList { get; set; } = true;
    public int FreeThresholdMb { get; set; } = 1024;
    public int StandbyMinimumMb { get; set; } = 1024;
    public int PurgeCooldownSeconds { get; set; } = 60;
    public int GovernorIntervalSeconds { get; set; } = 5;
}

public sealed class NetworkSettings
{
    /// <summary>Pausa o Windows Update (Delivery Optimization) e os downloads BITS enquanto um jogo está aberto.</summary>
    public bool PauseWindowsUpdate { get; set; } = true;
}

public sealed class AutoDetectSettings
{
    /// <summary>Trata como jogo o app em primeiro plano que cobre o monitor e desenha com DirectX.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Quanto tempo a tela cheia precisa ficar estável antes de virar jogo (evita alarme com algo maximizado de relance).</summary>
    public int ConfirmSeconds { get; set; } = 10;

    /// <summary>FPS mínimo pelo DirectX para confirmar (só com admin). Players de vídeo ficam em 24–30.</summary>
    public int MinFps { get; set; } = 20;

    /// <summary>Nunca tratar como jogo. Apps com regra de segundo plano também ficam de fora.</summary>
    public List<string> Exclusions { get; set; } = [.. DefaultConfig.AutoDetectExclusions];
}

public sealed class PowerSettings
{
    /// <summary>Plano de energia durante o jogo; null = não trocar. Ao fechar o jogo volta o plano de antes.</summary>
    public Guid? SchemeDuringGame { get; set; }
}

public sealed class FolgaConfig
{
    /// <summary>Versão do formato; arquivos antigos são migrados em <see cref="ConfigStore.Load"/>.</summary>
    public const int CurrentVersion = 5;

    public int Version { get; set; } = 1;
    public bool BoostEnabled { get; set; } = true;
    public List<GameProfile> Games { get; set; } = [];
    public List<BackgroundRule> Background { get; set; } = [];
    public MemorySettings Memory { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public AutoDetectSettings AutoDetect { get; set; } = new();
    public Overlay.OverlaySettings Overlay { get; set; } = new();
    public PowerSettings Power { get; set; } = new();

    public GameProfile? FindGame(string exe) => Games.FirstOrDefault(g => g.Matches(exe));
    public BackgroundRule? FindRule(string exe) => Background.FirstOrDefault(r => r.Matches(exe));

    /// <summary>O .exe já tem perfil, ativo ou não (desativado = o usuário não quer boost nele).</summary>
    public bool IsKnownGame(string exe) => Games.Any(g => ExeMatcher.MatchesAny(g.Executables, exe));

    public bool IsExcludedFromAutoDetect(string exe) =>
        ExeMatcher.MatchesAny(AutoDetect.Exclusions, exe) || FindRule(exe) is not null;
}

public static class ExeMatcher
{
    /// <summary>Compara o nome do .exe com um padrão simples (aceita * e ?), sem diferenciar maiúsculas.</summary>
    public static bool Matches(string pattern, string exe) =>
        !string.IsNullOrWhiteSpace(pattern) && FileSystemName.MatchesSimpleExpression(pattern.Trim(), exe, ignoreCase: true);

    public static bool MatchesAny(IEnumerable<string> patterns, string exe) => patterns.Any(p => Matches(p, exe));
}
