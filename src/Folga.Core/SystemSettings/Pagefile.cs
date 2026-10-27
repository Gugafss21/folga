using Folga.Core.Native;
using Microsoft.Win32;

namespace Folga.Core.SystemSettings;

public enum PagefileMode { SystemManaged, Custom, None }

/// <summary>
/// Arquivo de paginação como a tela "Memória virtual" do Windows o grava. <c>Drive</c> "?:" é o
/// "gerenciar automaticamente em todas as unidades".
/// </summary>
public sealed record PagefileSetting(PagefileMode Mode, string Drive = "C:", int InitialMb = 0, int MaximumMb = 0);

/// <summary>Quanto a paginação vale agora (desde o último boot), para orientar a escolha.</summary>
public readonly record struct CommitSnapshot(long RamBytes, long CommitLimitBytes, long CommitUsedBytes)
{
    public long PagefileBytes => Math.Max(0, CommitLimitBytes - RamBytes);
}

/// <summary>
/// Lê e grava PagingFiles (HKLM\...\Memory Management), o mesmo valor da tela "Memória virtual".
/// Vale a partir do próximo boot.
/// </summary>
public static class Pagefile
{
    private const string Key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string Value = "PagingFiles";

    public static PagefileSetting Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        return Parse(key?.GetValue(Value) as string[] ?? []);
    }

    /// <summary>Precisa de admin. Lança UnauthorizedAccessException sem ele.</summary>
    public static void Write(PagefileSetting setting)
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key, writable: true) ?? throw new InvalidOperationException("chave de memória não encontrada");
        key.SetValue(Value, Format(setting), RegistryValueKind.MultiString);
    }

    /// <summary>Primeira entrada vale; "C:\pagefile.sys 0 0" = tamanho gerenciado pelo Windows; vazio = sem paginação.</summary>
    public static PagefileSetting Parse(IEnumerable<string> entries)
    {
        var first = entries.Select(e => e.Trim()).FirstOrDefault(e => e.Length > 0);
        if (first is null) return new PagefileSetting(PagefileMode.None);

        var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var drive = parts[0].Length >= 2 && parts[0][1] == ':' ? parts[0][..2].ToUpperInvariant() : "C:";
        if (parts.Length >= 3 && int.TryParse(parts[1], out var initial) && int.TryParse(parts[2], out var maximum) && (initial > 0 || maximum > 0))
            return new PagefileSetting(PagefileMode.Custom, drive, initial, maximum);
        return new PagefileSetting(PagefileMode.SystemManaged, drive);
    }

    public static string[] Format(PagefileSetting s) => s.Mode switch
    {
        PagefileMode.None => [],
        PagefileMode.SystemManaged when s.Drive == "?:" => [@"?:\pagefile.sys"],
        PagefileMode.SystemManaged => [$@"{s.Drive}\pagefile.sys 0 0"],
        _ => [$@"{s.Drive}\pagefile.sys {s.InitialMb} {Math.Max(s.InitialMb, s.MaximumMb)}"],
    };

    public static unsafe CommitSnapshot Commit()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (!Kernel32.GlobalMemoryStatusEx(ref status)) return default;
        return new CommitSnapshot((long)status.ullTotalPhys, (long)status.ullTotalPageFile, (long)(status.ullTotalPageFile - status.ullAvailPageFile));
    }

    /// <summary>
    /// Aviso para a escolha, ou null se está tudo bem. Sem paginação, o limite de memória vira a RAM: jogo
    /// que passa disso fecha sozinho ("sem memória"), mesmo com RAM sobrando no Gerenciador de Tarefas.
    /// </summary>
    public static string? Warning(PagefileSetting s, CommitSnapshot commit)
    {
        var ramMb = commit.RamBytes >> 20;
        if (s.Mode == PagefileMode.None)
            return commit.CommitUsedBytes > commit.RamBytes * 0.8
                ? $"Agora mesmo o PC usa {commit.CommitUsedBytes >> 30} GB de memória reservada, perto dos {ramMb >> 10} GB de RAM: sem paginação, jogos e apps vão fechar por falta de memória."
                : $"Sem paginação o limite de memória vira os {ramMb >> 10} GB de RAM. Jogos grandes com navegador aberto podem fechar sozinhos por falta de memória.";
        if (s.Mode == PagefileMode.Custom && Math.Max(s.InitialMb, s.MaximumMb) < Math.Min(ramMb / 2, 8192))
            return $"Máximo de {Math.Max(s.InitialMb, s.MaximumMb)} MB é pouco para {ramMb >> 10} GB de RAM; abaixo de {Math.Min(ramMb / 2, 8192)} MB o limite de memória pode estourar em jogo.";
        if (s.Mode == PagefileMode.Custom && s.InitialMb < 16)
            return "O tamanho inicial precisa ser de pelo menos 16 MB.";
        return null;
    }
}
