using System.Diagnostics;
using Folga.Core.Network;

namespace Folga.Core.SystemSettings;

/// <summary>
/// Memória compactada do Windows (MMAgent): em vez de mandar páginas para o disco, o Windows as comprime
/// na RAM. Ligar/desligar pelo PowerShell (Enable/Disable-MMAgent), num processo à parte, para o Folga não
/// carregar o WMI. Vale a partir do próximo boot.
/// </summary>
public static class MemoryCompression
{
    /// <summary>O processo "Memory Compression" só existe com a compactação ativa neste boot.</summary>
    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("Memory Compression");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>O que está configurado para o próximo boot; null se não deu para ler (sem admin ou sem o módulo).</summary>
    public static bool? ReadConfigured()
    {
        var result = PowerShell.Run("(Get-MMAgent -ErrorAction Stop).MemoryCompression", 20_000);
        return result.Success && result.Output.Count > 0 && bool.TryParse(result.Output[^1].Trim(), out var on) ? on : null;
    }

    /// <summary>Precisa de admin. Devolve a mensagem de erro, ou null se deu certo.</summary>
    public static string? Set(bool enabled)
    {
        var result = PowerShell.Run($"{(enabled ? "Enable" : "Disable")}-MMAgent -MemoryCompression -ErrorAction Stop", 30_000);
        return result.Success ? null : FirstLine(result.Error);
    }

    internal static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "erro desconhecido";
}
