using System.Runtime.InteropServices;

namespace Folga.Core.Native;

public static class Privileges
{
    /// <summary>Privilégios que o app liga ao iniciar (todos já pertencem ao token de admin, só vêm desligados).</summary>
    public static readonly string[] Required =
    [
        "SeDebugPrivilege",                // abrir processos de outros usuários/serviços
        "SeIncreaseBasePriorityPrivilege", // teto de working set
        "SeIncreaseQuotaPrivilege",
        "SeProfileSingleProcessPrivilege", // limpar a standby list
    ];

    /// <summary>Liga os privilégios no token do próprio processo e devolve os que não puderam ser ligados.</summary>
    public static IReadOnlyList<string> Enable(IEnumerable<string> names)
    {
        var missing = new List<string>();
        if (!Advapi32.OpenProcessToken(Kernel32.GetCurrentProcess(), Advapi32.TOKEN_ADJUST_PRIVILEGES | Advapi32.TOKEN_QUERY, out var token))
            return names.ToList();
        try
        {
            foreach (var name in names)
            {
                if (!Advapi32.LookupPrivilegeValue(null, name, out var luid)) { missing.Add(name); continue; }
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = Advapi32.SE_PRIVILEGE_ENABLED };
                // AdjustTokenPrivileges devolve true mesmo sem o privilégio; o erro 1300 é que indica a falta.
                if (!Advapi32.AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0) || Marshal.GetLastPInvokeError() != 0)
                    missing.Add(name);
            }
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
        return missing;
    }
}
