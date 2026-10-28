using Microsoft.Win32;

namespace Folga.Core.Cleanup;

public enum CleanupKind
{
    UserTemp,
    WindowsTemp,
    WindowsUpdate,
    DeliveryOptimization,
    ErrorReports,
    CrashDumps,
    ThumbnailCache,
    DirectXShaderCache,
    GpuShaderCache,
    GpuInstallers,
}

/// <summary>
/// Uma categoria de limpeza: pastas fixas do Windows ou de drivers, nunca pastas pessoais. Só apaga arquivos com
/// mais de <see cref="MinAge"/> que casem com <see cref="Pattern"/>; com <see cref="Recursive"/>, também nas
/// subpastas (sem seguir atalhos de pasta). A própria pasta raiz nunca é apagada.
/// </summary>
public sealed record CleanupTarget(
    CleanupKind Kind,
    IReadOnlyList<string> Folders,
    TimeSpan MinAge,
    bool Recursive = true,
    string Pattern = "*",
    IReadOnlyList<string>? Files = null,
    bool NeedsAdmin = false,
    bool SelectedByDefault = true);

public sealed record CleanupScan(CleanupTarget Target, long Bytes, int FileCount, string? Blocked);

/// <summary>Blocked: não limpou nada porque a categoria estava bloqueada na hora (ver <see cref="DiskCleaner.Blocked"/>).</summary>
public sealed record CleanupResult(CleanupKind Kind, long FreedBytes, int Deleted, int Skipped, string? Blocked = null);

/// <summary>
/// Limpeza de espaço só em caches e temporários do Windows e dos drivers. Arquivo pessoal não entra por
/// construção: as pastas são uma lista fixa (nenhuma de Documentos, Downloads, Área de Trabalho, Imagens, nem a
/// Lixeira), atalhos de pasta (junções e links) não são seguidos, e arquivo em uso é pulado.
/// </summary>
public static class DiskCleaner
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>As categorias deste PC (as pastas que não existem aparecem com 0 bytes).</summary>
    public static IReadOnlyList<CleanupTarget> Targets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string systemDrive = Path.GetPathRoot(windows) ?? @"C:\";

        return
        [
            new(CleanupKind.UserTemp, [Path.GetTempPath()], Day),
            new(CleanupKind.WindowsTemp, [Path.Combine(windows, "Temp")], Day, NeedsAdmin: true),
            // Mais de 1 dia: um download em andamento (que o BITS nem sempre prende) não é apagado no meio.
            new(CleanupKind.WindowsUpdate, [Path.Combine(windows, "SoftwareDistribution", "Download")], Day, NeedsAdmin: true),
            new(CleanupKind.DeliveryOptimization,
                [Path.Combine(windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")],
                Day, NeedsAdmin: true),
            new(CleanupKind.ErrorReports,
                [Path.Combine(common, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(common, @"Microsoft\Windows\WER\ReportQueue"),
                 Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue")],
                TimeSpan.Zero, NeedsAdmin: true),
            new(CleanupKind.CrashDumps, [Path.Combine(windows, "Minidump"), Path.Combine(local, "CrashDumps")], TimeSpan.Zero,
                Files: [Path.Combine(windows, "MEMORY.DMP")], NeedsAdmin: true),
            new(CleanupKind.ThumbnailCache, [Path.Combine(local, @"Microsoft\Windows\Explorer")], TimeSpan.Zero, Recursive: false, Pattern: "thumbcache_*.db"),
            new(CleanupKind.DirectXShaderCache, [Path.Combine(local, "D3DSCache")], TimeSpan.Zero, SelectedByDefault: false),
            new(CleanupKind.GpuShaderCache,
                [Path.Combine(local, @"NVIDIA\DXCache"), Path.Combine(local, @"NVIDIA\GLCache"), Path.Combine(common, @"NVIDIA Corporation\NV_Cache"),
                 Path.Combine(local, @"AMD\DxCache"), Path.Combine(local, @"AMD\DxcCache"), Path.Combine(local, @"AMD\VkCache"), Path.Combine(local, @"AMD\GLCache"),
                 Path.Combine(local, @"Intel\ShaderCache")],
                TimeSpan.Zero, SelectedByDefault: false),
            new(CleanupKind.GpuInstallers,
                [Path.Combine(systemDrive, @"NVIDIA\DisplayDriver"), Path.Combine(common, @"NVIDIA Corporation\Downloader")],
                TimeSpan.Zero, NeedsAdmin: true),
        ];
    }

    /// <summary>Por que a categoria não pode ser limpa agora; null se pode.</summary>
    public static string? Blocked(CleanupTarget target, bool isAdmin)
    {
        if (target.NeedsAdmin && !isAdmin) return "precisa do Folga como administrador";
        if (target.Kind == CleanupKind.WindowsUpdate && UpdateWaitingForRestart())
            return "uma atualização espera o PC reiniciar; reinicie antes de limpar";
        return null;
    }

    /// <summary>O Windows Update instalou algo e espera reiniciar: os arquivos baixados ainda podem ser usados.</summary>
    public static bool UpdateWaitingForRestart()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        return key is not null;
    }

    public static CleanupScan Scan(CleanupTarget target, DateTime nowUtc, bool isAdmin)
    {
        long bytes = 0;
        var count = 0;
        foreach (var file in Candidates(target, nowUtc))
        {
            bytes += file.Length;
            count++;
        }
        return new CleanupScan(target, bytes, count, Blocked(target, isAdmin));
    }

    /// <summary>Confere o bloqueio de novo na hora (a análise pode ser de minutos atrás) e apaga.</summary>
    public static CleanupResult Clean(CleanupTarget target, DateTime nowUtc, bool isAdmin, CancellationToken ct = default)
    {
        if (Blocked(target, isAdmin) is { } blocked) return new CleanupResult(target.Kind, 0, 0, 0, blocked);
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var file in Candidates(target, nowUtc).ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var length = file.Length;
                if (file.IsReadOnly) file.IsReadOnly = false;
                file.Delete();
                freed += length;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++; // em uso ou protegido: fica
            }
        }
        if (target.Recursive)
            foreach (var folder in target.Folders) RemoveEmptySubfolders(folder, nowUtc - target.MinAge);
        return new CleanupResult(target.Kind, freed, deleted, skipped);
    }

    /// <summary>Arquivos que a categoria apagaria: dentro das pastas dela, sem seguir junções ou links.</summary>
    private static IEnumerable<FileInfo> Candidates(CleanupTarget target, DateTime nowUtc)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = target.Recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            MatchType = MatchType.Win32,
        };
        foreach (var folder in target.Folders)
        {
            if (!IsSafeRoot(folder) || !Directory.Exists(folder) || IsReparsePoint(folder)) continue;
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(folder).EnumerateFiles(target.Pattern, options); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
                if (nowUtc - f.LastWriteTimeUtc >= target.MinAge) yield return f;
        }
        foreach (var path in target.Files ?? [])
        {
            FileInfo f;
            try { f = new FileInfo(path); if (!f.Exists || (f.Attributes & FileAttributes.ReparsePoint) != 0) continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (nowUtc - f.LastWriteTimeUtc >= target.MinAge) yield return f;
        }
    }

    /// <summary>
    /// Trava de segurança contra uma lista mal montada: nunca a raiz de um disco, a pasta do usuário, a do
    /// Windows ou pastas pessoais. Toda pasta da lista tem de estar pelo menos três níveis abaixo da raiz.
    /// </summary>
    internal static bool IsSafeRoot(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length < 3) return false;

        var forbidden = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
        }.Select(Environment.GetFolderPath).Where(p => p.Length > 0).Select(p => p.TrimEnd(Path.DirectorySeparatorChar));
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (forbidden.Append(downloads).Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase))) return false;

        // Pastas pessoais por dentro (ex.: algo em Documentos) também não.
        var personal = new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0).Append(downloads);
        return !personal.Any(p => full.StartsWith(p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Tira as subpastas que ficaram vazias e são mais velhas que o corte, de baixo para cima (uma pasta vazia
    /// recém-criada pode estar para ser usada). A raiz fica; junções não são seguidas.
    /// </summary>
    private static void RemoveEmptySubfolders(string root, DateTime olderThanUtc)
    {
        if (!IsSafeRoot(root) || !Directory.Exists(root) || IsReparsePoint(root)) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        List<string> folders;
        try { folders = Directory.EnumerateDirectories(root, "*", options).OrderByDescending(d => d.Length).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var dir in folders)
        {
            try
            {
                if (!IsReparsePoint(dir) && Directory.GetCreationTimeUtc(dir) <= olderThanUtc && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
