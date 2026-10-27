using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Folga.Core.Journal;
using Folga.Core.Logging;
using Folga.Core.Native;
using Folga.Core.Processes;

namespace Folga.Core.Network;

/// <summary>
/// Rede pelas ferramentas do próprio Windows: políticas de QoS (limite de upload, DSCP), parada do
/// Delivery Optimization e suspensão dos downloads BITS. Tudo roda numa fila própria, porque o
/// PowerShell leva 1–2 s para abrir e o motor não pode esperar.
/// </summary>
public sealed class WindowsNetworkControl : INetworkControl
{
    private const string DeliveryOptimization = "DoSvc";
    private static readonly TimeSpan GuardInterval = TimeSpan.FromSeconds(30);

    private readonly ChangeJournal _journal;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private Timer? _guard;
    private bool _paused; // só lido e escrito na fila

    public WindowsNetworkControl(ChangeJournal journal)
    {
        _journal = journal;
        _worker = new Thread(Run) { IsBackground = true, Name = "Folga rede" };
        _worker.Start();
    }

    public void Begin(NetworkPlan plan)
    {
        if (plan.IsEmpty) return;
        Enqueue(() => ApplyPlan(plan));
    }

    public void End() => Enqueue(Revert);

    private void Enqueue(Action action)
    {
        try { _queue.Add(action); }
        catch (InvalidOperationException) { } // fila já encerrada (app saindo)
    }

    /// <summary>Espera a fila terminar (até 30 s) para não sair do app com a rede limitada.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(30));
        _guard?.Dispose();
    }

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"Rede: {ex.Message}"); }
        }
    }

    // ---- aplicar ----

    private void ApplyPlan(NetworkPlan plan)
    {
        if (plan.Policies.Count > 0) ApplyQos(plan.Policies);
        if (plan.PauseWindowsUpdate)
        {
            _paused = true;
            StopDeliveryOptimization();
            SuspendBits();
            // O Windows Update pode religar o serviço sob demanda; enquanto o jogo durar, ele é parado de novo.
            _guard?.Dispose();
            _guard = new Timer(_ => Enqueue(() => { if (_paused) StopDeliveryOptimization(); }), null, GuardInterval, GuardInterval);
        }
    }

    private void ApplyQos(IReadOnlyList<QosPolicy> policies)
    {
        // Diário primeiro: se o app cair no meio, a limpeza na próxima abertura sabe o que remover.
        _journal.AddRange(policies.Select(p => new JournalEntry { Kind = ChangeKind.QosPolicy, ExeName = p.Name }).ToList());

        var script = new StringBuilder(PowerShell.QosHelpers);
        foreach (var p in policies)
            script.AppendLine($"Add-FolgaPolicy {PowerShell.Quote(p.Name)} {PowerShell.Quote(p.Exe)} {p.ThrottleBitsPerSecond ?? 0} {p.Dscp ?? -1}");

        var result = PowerShell.Run(script.ToString());
        var applied = result.Output.Count(l => l.StartsWith("ok", StringComparison.Ordinal));
        if (applied > 0) Log.Info($"QoS: {applied} políticas aplicadas ({string.Join(", ", policies.Select(Describe))})");
        foreach (var line in result.Output.Where(l => l.StartsWith("erro", StringComparison.Ordinal))) Log.Warn($"QoS: {line}");
        if (!result.Success) Log.Warn($"QoS: PowerShell falhou ({result.Error.Trim()})");
    }

    private void StopDeliveryOptimization()
    {
        var state = ServiceManager.GetState(DeliveryOptimization);
        if (state != ServiceManager.State.Running) return;

        // Diário antes de parar (crash no meio não deixa o serviço parado); se não parar, a entrada sai.
        JournalEntry? entry = null;
        if (!_journal.Entries.Any(e => e.Kind == ChangeKind.ServiceStopped && e.ExeName == DeliveryOptimization))
            _journal.Add(entry = new JournalEntry { Kind = ChangeKind.ServiceStopped, ExeName = DeliveryOptimization });

        if (ServiceManager.Stop(DeliveryOptimization, out var error))
        {
            Log.Info("Windows Update pausado (Delivery Optimization parado)");
        }
        else
        {
            if (entry is not null) _journal.RemoveWhere(e => e.Id == entry.Id);
            Log.Warn($"Não deu para parar o Delivery Optimization: {error}");
        }
    }

    private void SuspendBits()
    {
        var result = PowerShell.Run("""
            Import-Module BitsTransfer -ErrorAction Stop
            Get-BitsTransfer -AllUsers -ErrorAction SilentlyContinue |
                Where-Object { $_.JobState -in 'Transferring','Queued','Connecting','TransientError' } |
                ForEach-Object { try { Suspend-BitsTransfer -BitsJob $_ -ErrorAction Stop | Out-Null; "id $($_.JobId)" } catch { "erro $($_.Exception.Message)" } }
            """);
        var ids = result.Output.Where(l => l.StartsWith("id ", StringComparison.Ordinal)).Select(l => l[3..].Trim()).ToList();
        _journal.AddRange(ids.Select(id => new JournalEntry { Kind = ChangeKind.BitsSuspended, ExeName = id }).ToList());
        if (ids.Count > 0) Log.Info($"{ids.Count} downloads BITS suspensos");
        if (!result.Success) Log.Warn($"BITS: {result.Error.Trim()}");
    }

    // ---- desfazer ----

    private void Revert()
    {
        _guard?.Dispose();
        _guard = null;
        _paused = false;

        var entries = _journal.Entries.Where(e => ChangeKinds.IsNetwork(e.Kind)).ToList();
        if (entries.Count == 0) return;

        var policies = entries.Where(e => e.Kind == ChangeKind.QosPolicy).Select(e => e.ExeName).ToList();
        if (policies.Count > 0)
        {
            var names = string.Join(",", policies.Select(PowerShell.Quote));
            var result = PowerShell.Run(PowerShell.QosHelpers + $"Remove-FolgaPolicies @({names})");
            if (result.Success) Log.Info($"QoS: {policies.Count} políticas removidas");
            else Log.Warn($"QoS: falha ao remover ({result.Error.Trim()})");
        }

        var jobs = entries.Where(e => e.Kind == ChangeKind.BitsSuspended).Select(e => e.ExeName).ToList();
        if (jobs.Count > 0)
        {
            var ids = string.Join(",", jobs.Select(PowerShell.Quote));
            var result = PowerShell.Run($$"""
                Import-Module BitsTransfer -ErrorAction Stop
                $ids = @({{ids}})
                Get-BitsTransfer -AllUsers -ErrorAction SilentlyContinue |
                    Where-Object { $ids -contains "$($_.JobId)" } |
                    ForEach-Object { Resume-BitsTransfer -BitsJob $_ -Asynchronous -ErrorAction SilentlyContinue | Out-Null }
                """);
            if (result.Success) Log.Info($"{jobs.Count} downloads BITS retomados");
        }

        foreach (var service in entries.Where(e => e.Kind == ChangeKind.ServiceStopped))
        {
            if (ServiceManager.Start(service.ExeName, out var error)) Log.Info("Windows Update retomado (Delivery Optimization iniciado)");
            else Log.Warn($"Não deu para iniciar {service.ExeName}: {error}");
        }

        var done = entries.Select(e => e.Id).ToHashSet();
        _journal.RemoveWhere(e => done.Contains(e.Id));
    }

    private static string Describe(QosPolicy p) =>
        p.ThrottleBitsPerSecond is { } bps ? $"{p.Exe} ↑ {bps / 1000} kbit/s" : $"{p.Exe} DSCP {p.Dscp}";
}

/// <summary>Executa scripts no Windows PowerShell 5.1 (que traz os módulos NetQos e BitsTransfer).</summary>
internal static class PowerShell
{
    /// <summary>
    /// Tenta o ActiveStore (some ao reiniciar o PC: nada fica preso se o app cair) e cai para o
    /// armazenamento local se o ActiveStore não aceitar a política.
    /// </summary>
    public const string QosHelpers = """
        function Add-FolgaPolicy([string]$name, [string]$exe, [uint64]$bps, [int]$dscp) {
            $p = @{ Name = $name; AppPathNameMatchCondition = $exe; Confirm = $false; ErrorAction = 'Stop' }
            if ($bps -gt 0) { $p.ThrottleRateActionBitsPerSecond = $bps }
            if ($dscp -ge 0) { $p.DSCPAction = [sbyte]$dscp }
            foreach ($store in 'ActiveStore', 'localhost') {
                try { Remove-NetQosPolicy -Name $name -PolicyStore $store -Confirm:$false -ErrorAction SilentlyContinue } catch { }
            }
            try { New-NetQosPolicy @p -PolicyStore ActiveStore | Out-Null; "ok ActiveStore $name" }
            catch {
                try { New-NetQosPolicy @p -PolicyStore localhost | Out-Null; "ok localhost $name" }
                catch { "erro $name $($_.Exception.Message)" }
            }
        }
        function Remove-FolgaPolicies([string[]]$names) {
            foreach ($store in 'ActiveStore', 'localhost') {
                Get-NetQosPolicy -PolicyStore $store -ErrorAction SilentlyContinue |
                    Where-Object { $names -contains $_.Name } |
                    ForEach-Object { Remove-NetQosPolicy -Name $_.Name -PolicyStore $store -Confirm:$false -ErrorAction SilentlyContinue }
            }
        }

        """;

    public sealed record Result(bool Success, IReadOnlyList<string> Output, string Error);

    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    public static Result Run(string script, int timeoutMs = 60_000)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(path, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.WriteLine("[Console]::OutputEncoding = [Text.Encoding]::UTF8");
            process.StandardInput.Write(script);
            process.StandardInput.Close();
            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return new Result(false, [], "tempo esgotado");
            }
            var lines = stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var error = stderr.Result;
            return new Result(process.ExitCode == 0 && string.IsNullOrWhiteSpace(error), lines, error);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new Result(false, [], ex.Message);
        }
    }
}
