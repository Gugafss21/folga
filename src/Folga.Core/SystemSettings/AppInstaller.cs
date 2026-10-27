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
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
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

    /// <summary>Um cmd escondido espera uns 5 s (o Folga fechar) e apaga as pastas. Chamar logo antes de fechar.</summary>
    public static void DeleteAfterExit(IEnumerable<string> folders)
    {
        var commands = string.Join(" & ", folders.Select(f => $"rmdir /s /q \"{f}\""));
        if (commands.Length == 0) return;
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c ping 127.0.0.1 -n 6 > nul & {commands}")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
        });
    }

    /// <summary>Sobra de uma atualização (o .exe antigo renomeado enquanto rodava): apaga ao abrir.</summary>
    public static void CleanUpOldExe(string exePath)
    {
        var old = Path.ChangeExtension(exePath, ".old.exe");
        try { if (File.Exists(old)) File.Delete(old); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
