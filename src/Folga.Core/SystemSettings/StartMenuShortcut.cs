using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Folga.Core.SystemSettings;

/// <summary>Atalho "Folga" no Menu Iniciar do usuário, criado pelo IShellLink do Windows (o mesmo do Explorer).</summary>
public static class StartMenuShortcut
{
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Folga.lnk");

    /// <summary>Para onde o atalho aponta; null se não existe.</summary>
    public static string? Target() => Target(FilePath);

    public static void Create(string exePath) => Create(exePath, FilePath);

    public static void Remove()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    internal static string? Target(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0);
            var path = new StringBuilder(260);
            link.GetPath(path, path.Capacity, 0, 0);
            return path.ToString();
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    internal static void Create(string exePath, string lnkPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
            link.SetDescription("Folga: prioridade para o jogo e menos disputa dos apps de fundo");
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxIconPath, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
