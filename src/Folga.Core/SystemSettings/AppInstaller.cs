using System.Diagnostics;
using Microsoft.Win32;

namespace Folga.Core.SystemSettings;

/// <summary>
/// Instalação sem instalador separado: o próprio Folga.exe se copia para %LocalAppData%\Programs\Folga
/// (pasta do usuário, sem precisar de Arquivos de Programas), cria o atalho e aparece em
/// Configurações &gt; Aplicativos instalados, de onde dá para desinstalar.
/// </summary>
public static class AppInstaller
{
    public const string UninstallArgument = "--uninstall";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Folga";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Folga");

    public static string InstalledExe => Path.Combine(InstallDir, "Folga.exe");

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static bool IsInstalledCopy(string exePath) => StartupTask.SamePath(exePath, InstalledExe);

    /// <summary>Copia o .exe, registra a desinstalação e cria o atalho. Devolve o caminho instalado.</summary>
    public static string Install(string sourceExe, string version)
    {
        Directory.CreateDirectory(InstallDir);
        File.Copy(sourceExe, InstalledExe, overwrite: true);
        RegisterUninstall(version);
        StartMenuShortcut.Create(InstalledExe);
        // Quem já abria com o Windows continua abrindo, agora pela cópia instalada.
        if (StartupTask.RegisteredPath() is not null && StartupTask.Register(InstalledExe) is { } error)
            throw new InvalidOperationException($"tarefa de inicialização: {error}");
        return InstalledExe;
    }

    /// <summary>A entrada de Aplicativos instalados (também atualizada depois de cada atualização, pela versão).</summary>
    public static void RegisterUninstall(string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Folga");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Gustavo Henrique");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" {UninstallArgument}");
        if (key.GetValue("InstallDate") is null) key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd")); // a da 1ª instalação, não a da última abertura
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        if (File.Exists(InstalledExe)) key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    /// <summary>
    /// Tira tarefa, atalho e a entrada de Aplicativos instalados. Devolve as pastas a apagar depois que o
    /// Folga fechar (um .exe aberto não pode apagar a si mesmo): a do programa e, com
    /// <paramref name="removeData"/>, também configuração, logs e histórico.
    /// </summary>
    public static IReadOnlyList<string> Uninstall(bool removeData)
    {
        StartupTask.Remove();
        StartMenuShortcut.Remove();
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        var folders = new List<string> { InstallDir };
        if (removeData)
        {
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Folga"));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Folga"));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Folga"));
        }
        return folders.Where(Directory.Exists).ToList();
    }

    /// <summary>
    /// Um PowerShell escondido espera este processo sair (até 2 min) e apaga as pastas. Chamar logo antes de
    /// fechar. Esperar pelo processo, e não um tempo fixo, garante que o .exe já está solto.
    /// </summary>
    public static void DeleteAfterExit(IEnumerable<string> folders)
    {
        var list = string.Join(",", folders.Select(f => $"'{f.Replace("'", "''")}'"));
        if (list.Length == 0) return;
        var script = $"Wait-Process -Id {Environment.ProcessId} -Timeout 120 -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; Remove-Item -LiteralPath {list} -Recurse -Force -ErrorAction SilentlyContinue";
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
        });
    }

    /// <summary>
    /// Sobras de uma atualização, apagadas ao abrir: o .exe antigo renomeado enquanto rodava e uma versão
    /// nova baixada que não chegou a entrar (download interrompido ou adiado por causa de um jogo).
    /// </summary>
    public static void CleanUpOldExe(string exePath)
    {
        foreach (var file in new[] { Path.ChangeExtension(exePath, ".old.exe"), Updater.DownloadPath(exePath) })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
