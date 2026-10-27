namespace Folga.Core.Profiles;

public static class DefaultConfig
{
    // Upload padrão durante o jogo: o suficiente para uso normal, pouco para encher o buffer do roteador (bufferbloat).
    internal const int BrowserUploadKbps = 4000;
    internal const int SyncUploadKbps = 1000;

    public static FolgaConfig Create() => new()
    {
        Version = FolgaConfig.CurrentVersion,
        Games =
        [
            // Nome do .exe a confirmar com o jogo aberto; o launcher do EAC (start_protected_game.exe) fica de fora.
            new GameProfile { Name = "Arc Raiders", Executables = ["PioneerGame*.exe", "ArcRaiders*.exe"] },
        ],
        Background =
        [
            Browser("Chrome", "chrome.exe"),
            Browser("Edge", "msedge.exe"),
            Browser("Firefox", "firefox.exe"),
            Browser("Opera / Brave", "opera.exe", "brave.exe"),
            // EcoQoS e limite de upload desligados no Discord: voz e transmissão precisam dos dois.
            new BackgroundRule { Name = "Discord", Executables = ["Discord.exe"], EcoQos = false },
            // steam.exe fica de fora: o jogo conversa com ele (Steam API, rede). Só o navegador embutido é segurado.
            new BackgroundRule { Name = "Steam (navegador)", Executables = ["steamwebhelper.exe"] },
            new BackgroundRule { Name = "Epic Games", Executables = ["EpicGamesLauncher.exe", "EpicWebHelper.exe"] },
            new BackgroundRule { Name = "OneDrive", Executables = ["OneDrive.exe"], UploadLimitKbps = SyncUploadKbps },
            new BackgroundRule { Name = "WebView2", Executables = ["msedgewebview2.exe"] },
        ],
    };

    /// <summary>
    /// Apps que ficam em tela cheia sem ser jogo: navegadores, players, chamadas, apresentações, launchers e
    /// o próprio Windows. Apps com regra de segundo plano já ficam de fora sem estar aqui.
    /// </summary>
    internal static readonly string[] AutoDetectExclusions =
    [
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe",
        "vlc.exe", "mpc-hc*.exe", "mpc-be*.exe", "PotPlayer*.exe", "mpv.exe", "wmplayer.exe", "Video.UI.exe", "Microsoft.Media.Player.exe",
        "Spotify.exe", "Netflix*.exe", "Discord.exe", "Teams.exe", "ms-teams.exe", "Zoom.exe", "obs64.exe",
        "POWERPNT.EXE", "Code.exe", "devenv.exe",
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "Battle.net.exe", "EADesktop.exe", "GalaxyClient.exe", "upc.exe",
        "ApplicationFrameHost.exe", "msedgewebview2.exe", "Folga.exe",
    ];

    /// <summary>Leva um config antigo ao formato atual sem perder o que o usuário mudou.</summary>
    public static bool Migrate(FolgaConfig config)
    {
        if (config.Version >= FolgaConfig.CurrentVersion) return false;

        if (config.Version < 2)
        {
            // v2: limite de upload. Só preenche regras padrão que ainda estão sem limite.
            foreach (var rule in config.Background.Where(r => r.UploadLimitKbps == 0))
            {
                if (rule.MemoryBudgetMb > 0 && IsBrowser(rule)) rule.UploadLimitKbps = BrowserUploadKbps;
                else if (rule.Executables.Any(e => e.Equals("OneDrive.exe", StringComparison.OrdinalIgnoreCase))) rule.UploadLimitKbps = SyncUploadKbps;
            }
        }
        // v3: detecção automática. A seção ausente no arquivo já vem com o padrão (ligada, exclusões padrão).
        config.Version = FolgaConfig.CurrentVersion;
        return true;
    }

    private static readonly string[] BrowserExes = ["chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe"];

    private static bool IsBrowser(BackgroundRule rule) =>
        rule.Executables.Any(e => BrowserExes.Contains(e, StringComparer.OrdinalIgnoreCase));

    private static BackgroundRule Browser(string name, params string[] exes) =>
        new() { Name = name, Executables = [.. exes], MemoryBudgetMb = 2048, UploadLimitKbps = BrowserUploadKbps };
}
