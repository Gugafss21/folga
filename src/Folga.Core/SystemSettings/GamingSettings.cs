using Microsoft.Win32;

namespace Folga.Core.SystemSettings;

/// <summary>Os ajustes de jogo do próprio Windows, como as telas de Configurações os gravam no registro.</summary>
public sealed record GamingSnapshot(bool GameMode, bool WindowedOptimizations, bool GameBarCapture, bool? HardwareGpuScheduling);

public static class GamingSettings
{
    private const string GameBar = @"Software\Microsoft\GameBar";
    private const string DirectX = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string DirectXValue = "DirectXUserGlobalSettings";
    private const string SwapEffectUpgrade = "SwapEffectUpgradeEnable";
    private const string GameConfigStore = @"System\GameConfigStore";
    private const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    /// <summary>Valores ausentes valem o padrão do Windows: Modo de Jogo e capturas ligados, otimizações de janela desligadas.</summary>
    public static GamingSnapshot Read() => new(
        GameMode: Dword(Registry.CurrentUser, GameBar, "AutoGameModeEnabled") != 0,
        WindowedOptimizations: ParseDirectX(String(Registry.CurrentUser, DirectX, DirectXValue)).GetValueOrDefault(SwapEffectUpgrade) == "1",
        GameBarCapture: Dword(Registry.CurrentUser, GameConfigStore, "GameDVR_Enabled") != 0 && Dword(Registry.CurrentUser, GameDvr, "AppCaptureEnabled") != 0,
        HardwareGpuScheduling: Dword(Registry.LocalMachine, GraphicsDrivers, "HwSchMode", -1) switch { 2 => true, 1 => false, _ => null });

    /// <summary>Configurações > Jogos > Modo de Jogo. Vale na hora.</summary>
    public static void SetGameMode(bool on) => SetDword(Registry.CurrentUser, GameBar, "AutoGameModeEnabled", on ? 1 : 0);

    /// <summary>Configurações > Tela > Gráficos > Otimizações para jogos em janela. Vale ao abrir o jogo de novo.</summary>
    public static void SetWindowedOptimizations(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(DirectX);
        var settings = ParseDirectX(key.GetValue(DirectXValue) as string);
        settings[SwapEffectUpgrade] = on ? "1" : "0";
        key.SetValue(DirectXValue, FormatDirectX(settings), RegistryValueKind.String);
    }

    /// <summary>Capturas do Xbox Game Bar (inclui a gravação em segundo plano). Vale ao abrir o jogo de novo.</summary>
    public static void SetGameBarCapture(bool on)
    {
        SetDword(Registry.CurrentUser, GameConfigStore, "GameDVR_Enabled", on ? 1 : 0);
        SetDword(Registry.CurrentUser, GameDvr, "AppCaptureEnabled", on ? 1 : 0);
        if (!on) SetDword(Registry.CurrentUser, GameDvr, "HistoricalCaptureEnabled", 0);
    }

    /// <summary>Agendamento de GPU acelerado por hardware (HwSchMode 2 = ligado, 1 = desligado). Precisa de admin e de reiniciar.</summary>
    public static void SetHardwareGpuScheduling(bool on) => SetDword(Registry.LocalMachine, GraphicsDrivers, "HwSchMode", on ? 2 : 1);

    /// <summary>"Chave=valor;Chave=valor;" do DirectXUserGlobalSettings, mantendo a ordem e o que o Folga não mexe (ex.: AutoHDREnable).</summary>
    public static Dictionary<string, string> ParseDirectX(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) result[pair[..eq]] = pair[(eq + 1)..];
        }
        return result;
    }

    public static string FormatDirectX(Dictionary<string, string> settings) => string.Concat(settings.Select(kv => $"{kv.Key}={kv.Value};"));

    private static int Dword(RegistryKey root, string path, string name, int missing = 1)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) is int v ? v : missing;
    }

    private static string? String(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private static void SetDword(RegistryKey root, string path, string name, int value)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}
