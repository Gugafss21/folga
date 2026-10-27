using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace Folga.Core.SystemSettings;

/// <summary>
/// Tarefa agendada "Folga": abre o Folga minimizado na bandeja ao entrar no Windows, já como
/// administrador e sem a pergunta do UAC (é o jeito do Windows para isso; a pasta Inicializar
/// pediria confirmação a cada boot). Criar ou apagar a tarefa precisa de admin.
/// </summary>
public static class StartupTask
{
    public const string Name = "Folga";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Caminho do .exe que a tarefa abre; null se a tarefa não existe.</summary>
    public static string? RegisteredPath()
    {
        var (exit, output, _) = Schtasks($"/Query /TN \"{Name}\" /XML");
        return exit == 0 ? CommandOf(output) : null;
    }

    /// <summary>Cria ou substitui a tarefa. Devolve a mensagem de erro, ou null se deu certo.</summary>
    public static string? Register(string exePath)
    {
        var file = Path.Combine(Path.GetTempPath(), $"folga-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, BuildXml(exePath, WindowsIdentity.GetCurrent().Name), Encoding.Unicode);
            var (exit, _, error) = Schtasks($"/Create /TN \"{Name}\" /XML \"{file}\" /F");
            return exit == 0 ? null : FirstLine(error);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static string? Remove()
    {
        var (exit, _, error) = Schtasks($"/Delete /TN \"{Name}\" /F");
        return exit == 0 ? null : FirstLine(error);
    }

    /// <summary>
    /// Ao entrar no Windows (10 s depois, para não disputar o boot), com privilégio máximo. Prioridade 4 =
    /// normal: o padrão das tarefas (7) deixaria o Folga em prioridade abaixo do normal. Sem limite de
    /// tempo e sem parar na bateria.
    /// </summary>
    public static string BuildXml(string exePath, string userId) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Abre o Folga minimizado na bandeja ao entrar no Windows, já como administrador.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <Delay>PT10S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"{SecurityElement.Escape(exePath)}"</Command>
              <Arguments>--minimized</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>O .exe de uma tarefa em XML (sem aspas); null se não der para ler.</summary>
    public static string? CommandOf(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Descendants(Ns + "Command").FirstOrDefault()?.Value.Trim().Trim('"');
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static (int Exit, string Output, string Error) Schtasks(string arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                process.Kill();
                return (-1, "", "tempo esgotado");
            }
            return (process.ExitCode, output.Result, error.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private static string FirstLine(string text) => MemoryCompression.FirstLine(text);
}
