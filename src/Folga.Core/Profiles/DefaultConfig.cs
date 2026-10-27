namespace Folga.Core.Profiles;

public static class DefaultConfig
{
    public static FolgaConfig Create() => new()
    {
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
            // EcoQoS desligado no Discord: pode picotar a voz.
            new BackgroundRule { Name = "Discord", Executables = ["Discord.exe"], EcoQos = false },
            // steam.exe fica de fora: o jogo conversa com ele (Steam API, rede). Só o navegador embutido é segurado.
            new BackgroundRule { Name = "Steam (navegador)", Executables = ["steamwebhelper.exe"] },
            new BackgroundRule { Name = "Epic Games", Executables = ["EpicGamesLauncher.exe", "EpicWebHelper.exe"] },
            new BackgroundRule { Name = "OneDrive", Executables = ["OneDrive.exe"] },
            new BackgroundRule { Name = "WebView2", Executables = ["msedgewebview2.exe"] },
        ],
    };

    private static BackgroundRule Browser(string name, params string[] exes) =>
        new() { Name = name, Executables = [.. exes], MemoryBudgetMb = 2048 };
}
