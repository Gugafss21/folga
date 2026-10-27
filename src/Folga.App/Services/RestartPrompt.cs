using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Folga.App.Services;

/// <summary>
/// O diálogo padrão do Windows "Você precisa reiniciar o computador para aplicar estas alterações.
/// Reiniciar agora?" (shell32 RestartDialogEx, o mesmo das Propriedades do Sistema). Se a pessoa
/// aceitar, o próprio Windows reinicia, e o Folga desfaz o que estiver aplicado ao receber o aviso de logoff.
/// </summary>
public static partial class RestartPrompt
{
    private const uint EWX_REBOOT = 0x2;
    private const uint SHTDN_REASON_MAJOR_OPERATINGSYSTEM = 0x00020000;
    private const uint SHTDN_REASON_MINOR_RECONFIG = 0x00000004;
    private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    /// <summary>true se a pessoa escolheu reiniciar.</summary>
    public static bool Show(Window owner) =>
        RestartDialogEx(new WindowInteropHelper(owner).Handle, null, EWX_REBOOT,
            SHTDN_REASON_MAJOR_OPERATINGSYSTEM | SHTDN_REASON_MINOR_RECONFIG | SHTDN_REASON_FLAG_PLANNED) == 6; // IDYES

    [LibraryImport("shell32.dll", EntryPoint = "#730", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RestartDialogEx(nint owner, string? prompt, uint returnFlags, uint reason);
}
