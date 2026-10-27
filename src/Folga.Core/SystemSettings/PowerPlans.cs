using System.Runtime.InteropServices;

namespace Folga.Core.SystemSettings;

public sealed record PowerScheme(Guid Id, string Name);

public interface IPowerPlan
{
    Guid? Active();
    bool SetActive(Guid scheme);
    IReadOnlyList<PowerScheme> List();
}

/// <summary>Planos de energia pelo powrprof.dll (o mesmo que o powercfg usa), sem abrir processo.</summary>
public sealed unsafe partial class WindowsPowerPlan : IPowerPlan
{
    private const uint ACCESS_SCHEME = 16;

    public Guid? Active()
    {
        if (PowerGetActiveScheme(0, out var ptr) != 0) return null;
        try { return *(Guid*)ptr; }
        finally { LocalFree(ptr); }
    }

    public bool SetActive(Guid scheme) => PowerSetActiveScheme(0, &scheme) == 0;

    public IReadOnlyList<PowerScheme> List()
    {
        var result = new List<PowerScheme>();
        for (uint i = 0; ; i++)
        {
            Guid id;
            var size = (uint)sizeof(Guid);
            if (PowerEnumerate(0, null, null, ACCESS_SCHEME, i, (byte*)&id, ref size) != 0) break;
            result.Add(new PowerScheme(id, FriendlyName(id) ?? id.ToString()));
        }
        return result;
    }

    private static string? FriendlyName(Guid id)
    {
        uint size = 0;
        if (PowerReadFriendlyName(0, &id, null, null, null, ref size) != 0 || size == 0) return null;
        var buffer = stackalloc byte[(int)size];
        return PowerReadFriendlyName(0, &id, null, null, buffer, ref size) == 0 ? new string((char*)buffer).TrimEnd('\0') : null;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint root, out nint scheme);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(nint root, Guid* scheme);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(nint root, Guid* scheme, Guid* subgroup, uint accessFlags, uint index, byte* buffer, ref uint size);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(nint root, Guid* scheme, Guid* subgroup, Guid* setting, byte* buffer, ref uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
