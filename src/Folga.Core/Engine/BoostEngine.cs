using Folga.Core.Cpu;
using Folga.Core.Detection;
using Folga.Core.Journal;
using Folga.Core.Logging;
using Folga.Core.Memory;
using Folga.Core.Network;
using Folga.Core.Processes;
using Folga.Core.Profiles;
using Folga.Core.SystemSettings;

namespace Folga.Core.Engine;

public sealed record GameStatus(string ProfileName, ProcessRef Process, string Note, GameProfile Profile);

public sealed record EngineState(
    bool Enabled,
    IReadOnlyList<GameStatus> Games,
    int HeldProcesses,
    int TrimCount,
    int PurgeCount,
    DateTimeOffset? LastPurge,
    MemorySnapshot? Memory,
    bool MeasureOnly = false)
{
    public bool SessionActive => Games.Count > 0;
}

/// <summary>
/// Orquestra a sessão de jogo: quando um jogo de perfil abre, dá prioridade a ele e segura os apps
/// de fundo; quando o último processo do jogo fecha, desfaz tudo a partir do diário.
/// </summary>
public sealed class BoostEngine : IDisposable
{
    // Processos esvaziados há pouco não são esvaziados de novo (evita ficar paginando sem parar).
    private static readonly TimeSpan TrimCooldown = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly IProcessControl _control;
    private readonly IIfeoStore _ifeo;
    private readonly ISystemMemory _memory;
    private readonly ChangeJournal _journal;
    private readonly IProcessWatcher _watcher;
    private readonly INetworkControl _network;
    private readonly IPowerPlan? _power;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, (GameProfile Profile, ProcessRef Process, string Note)> _games = [];
    private readonly List<int> _gameOrder = []; // ordem de abertura (o Dictionary reaproveita vagas depois de remover)
    private readonly Dictionary<int, (BackgroundRule Rule, ProcessRef Process)> _background = [];
    private readonly Dictionary<int, DateTimeOffset> _lastTrim = [];
    private FolgaConfig _config;
    private ITimer? _tick;
    private int _trimCount, _purgeCount;
    private DateTimeOffset? _lastPurge;
    private MemorySnapshot? _lastMemory;
    private bool _disposed;

    public CpuTopology Topology { get; }

    public event Action? StateChanged;

    public BoostEngine(FolgaConfig config, CpuTopology topology, IProcessControl control, IIfeoStore ifeo,
        ISystemMemory memory, ChangeJournal journal, IProcessWatcher watcher, INetworkControl network, TimeProvider? time = null,
        IPowerPlan? power = null)
    {
        _power = power;
        _config = config;
        Topology = topology;
        _control = control;
        _ifeo = ifeo;
        _memory = memory;
        _journal = journal;
        _watcher = watcher;
        _network = network;
        _time = time ?? TimeProvider.System;
    }

    public FolgaConfig Config
    {
        get { lock (_gate) return _config; }
    }

    public void Start()
    {
        lock (_gate)
        {
            RecoverFromCrash();
            _watcher.Started += OnProcessStarted;
            _watcher.Stopped += OnProcessStopped;
            _watcher.Start();
            Rescan();
        }
        Notify();
    }

    public EngineState GetState()
    {
        lock (_gate)
        {
            return new EngineState(
                _config.BoostEnabled,
                OpenGames.Select(g => new GameStatus(g.Profile.Name, g.Process, g.Note, g.Profile)).ToList(),
                _background.Count, _trimCount, _purgeCount, _lastPurge, _lastMemory, _config.MeasureOnly);
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_config.BoostEnabled == enabled) return;
            _config.BoostEnabled = enabled;
            Log.Info(enabled ? "Boost ligado" : "Boost desligado");
            if (enabled) Rescan();
            else EndSession();
        }
        Notify();
    }

    /// <summary>Troca perfis/regras: desfaz a sessão atual e reaplica com a configuração nova.</summary>
    public void ApplyConfig(FolgaConfig config)
    {
        lock (_gate)
        {
            EndSession();
            _config = config;
            Rescan();
        }
        Notify();
    }

    /// <summary>Liga/desliga o "só medir". Com um jogo aberto, desfaz ou aplica o boost na hora.</summary>
    public void SetMeasureOnly(bool measureOnly)
    {
        lock (_gate)
        {
            if (_config.MeasureOnly == measureOnly) return;
            EndSession();
            _config.MeasureOnly = measureOnly;
            Log.Info(measureOnly ? "Só medir: o jogo é medido, nada é alterado" : "Só medir desligado: boost normal");
            Rescan();
        }
        Notify();
    }

    /// <summary>
    /// Muda ajustes que não mexem na sessão aberta (overlay, plano de energia do próximo jogo, atualizações),
    /// sem desfazer e reaplicar o boost como <see cref="ApplyConfig"/> faz.
    /// </summary>
    public void ChangeSetting(Action<FolgaConfig> edit)
    {
        lock (_gate) edit(_config);
    }

    /// <summary>Acrescenta um perfil (o jogo detectado automaticamente) e já aplica no processo aberto, sem desfazer nada.</summary>
    public void AddGameProfile(GameProfile profile)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _config.Games.Add(profile);
            Rescan();
        }
        Notify();
    }

    /// <summary>Desfaz tudo, inclusive o IFEO, e desliga o boost.</summary>
    public void RestoreAll()
    {
        lock (_gate)
        {
            _config.BoostEnabled = false;
            EndSession();
            foreach (var entry in _journal.Entries.Where(e => e.Kind == ChangeKind.Ifeo)) RevertIfeo(entry);
            _journal.RemoveWhere(e => e.Kind == ChangeKind.Ifeo);
            Log.Info("Restaurar tudo: alterações desfeitas, boost desligado");
        }
        Notify();
    }

    /// <summary>Roda o governador de RAM e a limpeza de standby uma vez (o timer chama isso; os testes também).</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _lastMemory = _memory.Read();
            if (_games.Count == 0) return;
            GovernMemory();
            PurgeStandbyIfNeeded(_lastMemory.Value);
        }
        Notify();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _watcher.Started -= OnProcessStarted;
            _watcher.Stopped -= OnProcessStopped;
            EndSession();
        }
        _watcher.Dispose();
        _network.Dispose(); // espera a rede ser desfeita antes de o app sair
    }

    // ---- eventos de processo ----

    private void OnProcessStarted(int pid, string exe)
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed || !_config.BoostEnabled) return;
            if (_config.FindGame(exe) is { } profile)
            {
                changed = AddGame(pid, exe, profile);
            }
            else if (_games.Count > 0 && !_config.MeasureOnly && _config.FindRule(exe) is { } rule && Primary!.AppliesRule(rule))
            {
                changed = HoldBackground(pid, exe, rule);
            }
        }
        if (changed) Notify();
    }

    private void OnProcessStopped(int pid)
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (_games.Remove(pid, out var game))
            {
                _gameOrder.Remove(pid);
                Log.Info($"{game.Profile.Name} fechou ({game.Process.ExeName}, PID {pid})");
                _journal.RemoveWhere(e => ChangeKinds.IsProcess(e.Kind) && e.Is(game.Process));
                if (_games.Count == 0) EndSession();
                changed = true;
            }
            else if (_background.Remove(pid, out var bg))
            {
                _lastTrim.Remove(pid);
                _journal.RemoveWhere(e => ChangeKinds.IsProcess(e.Kind) && e.Is(bg.Process));
                changed = true;
            }
        }
        if (changed) Notify();
    }

    // ---- sessão ----

    /// <summary>O primeiro jogo da sessão: o perfil dele manda nos ajustes por jogo.</summary>
    private GameProfile? Primary => _gameOrder.Count > 0 ? _games[_gameOrder[0]].Profile : null;

    /// <summary>Os jogos abertos na ordem em que abriram.</summary>
    private IEnumerable<(GameProfile Profile, ProcessRef Process, string Note)> OpenGames => _gameOrder.Select(pid => _games[pid]);

    private void Rescan()
    {
        if (!_config.BoostEnabled) return;
        var snapshot = _control.Snapshot();
        foreach (var p in snapshot)
        {
            if (_config.FindGame(p.ExeName) is { } profile) AddGame(p.Pid, p.ExeName, profile, snapshot);
        }
    }

    private bool AddGame(int pid, string exe, GameProfile profile, IReadOnlyList<ProcessEntry>? snapshot = null)
    {
        if (_games.ContainsKey(pid)) return false;
        if (_control.Resolve(pid, exe) is not { } process) return false;

        var note = _config.MeasureOnly ? "Só medindo: nada foi alterado." : ApplyGameTweaks(process, profile);
        _games[pid] = (profile, process, note);
        _gameOrder.Add(pid);
        Log.Info($"Jogo detectado: {profile.Name} ({exe}, PID {pid}). {note}");

        if (_games.Count == 1) BeginSession(snapshot ?? _control.Snapshot());
        return true;
    }

    private void BeginSession(IReadOnlyList<ProcessEntry> snapshot)
    {
        if (_config.MeasureOnly)
        {
            Log.Info("Sessão iniciada só para medir: nada alterado");
            return;
        }
        var primary = Primary!;
        foreach (var p in snapshot)
        {
            if (_games.ContainsKey(p.Pid) || _config.FindGame(p.ExeName) is not null) continue;
            if (_config.FindRule(p.ExeName) is { } rule && primary.AppliesRule(rule)) HoldBackground(p.Pid, p.ExeName, rule);
        }
        var interval = TimeSpan.FromSeconds(Math.Max(1, _config.Memory.GovernorIntervalSeconds));
        _tick?.Dispose();
        _tick = _time.CreateTimer(_ => Tick(), null, interval, interval);
        _network.Begin(NetworkPlanner.Build(_config, OpenGames.Select(g => (g.Profile, g.Process.ExeName))));
        SwitchPowerScheme();
        Log.Info($"Sessão iniciada: {_background.Count} processos de fundo segurados");
    }

    /// <summary>Troca para o plano escolhido para o jogo, guardando o de antes no diário.</summary>
    private void SwitchPowerScheme()
    {
        if (_power is null || _config.PowerSchemeFor(Primary) is not { } target) return;
        if (_power.Active() is not { } current || current == target) return;
        if (_power.List().FirstOrDefault(s => s.Id == target) is not { } scheme)
        {
            Log.Warn($"Plano de energia {target} não existe mais; mantendo o atual");
            return;
        }
        var entry = new JournalEntry { Kind = ChangeKind.PowerScheme, ExeName = current.ToString() };
        _journal.Add(entry);
        if (_power.SetActive(target)) Log.Info($"Plano de energia: {scheme.Name} durante o jogo");
        else
        {
            _journal.RemoveWhere(e => e.Id == entry.Id);
            Log.Warn($"Não deu para trocar o plano de energia para {scheme.Name}");
        }
    }

    private void RestorePowerScheme()
    {
        var entries = _journal.Entries.Where(e => e.Kind == ChangeKind.PowerScheme).ToList();
        if (entries.Count == 0) return;
        // A primeira troca guarda o plano de antes do jogo.
        if (_power is not null && Guid.TryParse(entries[0].ExeName, out var original))
        {
            if (_power.SetActive(original)) Log.Info("Plano de energia de antes do jogo restaurado");
            else Log.Warn($"Não deu para voltar ao plano de energia {original}");
        }
        _journal.RemoveWhere(e => e.Kind == ChangeKind.PowerScheme);
    }

    private void EndSession()
    {
        _tick?.Dispose();
        _tick = null;
        _games.Clear();
        _gameOrder.Clear();
        _background.Clear();
        _lastTrim.Clear();
        _network.End();
        RestorePowerScheme();

        var reverted = 0;
        var entries = _journal.Entries.Where(e => ChangeKinds.IsProcess(e.Kind)).ToList();
        foreach (var entry in entries)
        {
            var result = _control.Write(entry.Process, entry.Kind, entry.Original);
            if (result == TweakResult.Ok) reverted++;
            else if (result != TweakResult.Gone) Log.Warn($"Não deu para desfazer {entry.Kind} em {entry.ExeName} (PID {entry.Pid}): {result}");
        }
        var ids = entries.Select(e => e.Id).ToHashSet();
        _journal.RemoveWhere(e => ids.Contains(e.Id));
        if (entries.Count > 0) Log.Info($"Sessão encerrada: {reverted} alterações desfeitas");
    }

    /// <summary>Sobrou coisa no diário (o app fechou no meio de um jogo): desfaz o que ainda existe. O IFEO fica.</summary>
    private void RecoverFromCrash()
    {
        var pending = _journal.Entries.Where(e => e.Kind != ChangeKind.Ifeo).ToList();
        // Processos e rede: EndSession desfaz os dois (a rede na fila do INetworkControl).
        if (pending.Count == 0) return;
        Log.Warn($"Diário com {pending.Count} alterações de uma sessão anterior; restaurando");
        EndSession();
    }

    // ---- aplicação ----

    private string ApplyGameTweaks(ProcessRef process, GameProfile profile)
    {
        var tweaks = new List<(ChangeKind, long[])> { (ChangeKind.Priority, [PriorityMapping.ToWin32(profile.Priority)]) };
        var gameIds = Topology.GameIds(profile.Cores, BackgroundCores.Auto, profile.CustomCores);
        if (gameIds.Count > 0) tweaks.Add((ChangeKind.CpuSets, gameIds.Select(i => (long)i).ToArray()));

        var results = Apply(process, tweaks);
        var cores = !results.TryGetValue(ChangeKind.CpuSets, out var c) ? ""
            : c == TweakResult.Ok ? $" Núcleos: {CoreList(gameIds)}."
            : c == TweakResult.AccessDenied ? " Núcleos não aplicados: o anti-cheat negou; o jogo usa todos."
            : $" Núcleos não aplicados ({c}).";
        var priority = results[ChangeKind.Priority];
        if (priority == TweakResult.Ok)
            return $"Prioridade {PriorityMapping.Label(profile.Priority)}.{cores}";

        if (priority == TweakResult.AccessDenied && profile.UseIfeoFallback)
            return ApplyIfeo(process.ExeName, profile.Priority) + cores;

        return $"Prioridade não aplicada ({priority}).{cores}";
    }

    /// <summary>"1, 2 e 3": os núcleos físicos (contados a partir de 1) que têm alguma das threads.</summary>
    private string CoreList(IReadOnlyCollection<uint> ids)
    {
        var numbers = Topology.Cores.Where(core => core.Ids.Any(ids.Contains)).Select(core => (core.Index + 1).ToString()).ToList();
        return numbers.Count <= 1 ? string.Concat(numbers) : $"{string.Join(", ", numbers[..^1])} e {numbers[^1]}";
    }

    private string ApplyIfeo(string exe, PriorityLevel level)
    {
        var target = PriorityMapping.ToIfeo(level);
        var current = _ifeo.Get(exe);
        if (current == target)
            return "Anti-cheat bloqueia acesso; prioridade vem do IFEO, aplicada pelo Windows ao abrir.";

        JournalEntry? entry = null;
        if (_journal.FindIfeo(exe) is null)
        {
            entry = new JournalEntry { Kind = ChangeKind.Ifeo, ExeName = exe, Original = current is int c ? [c] : [], Applied = [target] };
            _journal.Add(entry);
        }
        try
        {
            _ifeo.Set(exe, target);
            Log.Info($"IFEO gravado para {exe} (CpuPriorityClass={target})");
            return "Anti-cheat bloqueou o acesso; prioridade gravada no IFEO, vale a partir da próxima abertura.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            if (entry is not null) _journal.RemoveWhere(e => e.Id == entry.Id);
            Log.Error($"Falha ao gravar IFEO para {exe}: {ex.Message}");
            return "Anti-cheat bloqueou o acesso e o IFEO não pôde ser gravado.";
        }
    }

    private bool HoldBackground(int pid, string exe, BackgroundRule rule)
    {
        if (_background.ContainsKey(pid)) return false;
        if (_control.Resolve(pid, exe) is not { } process) return false;

        var tweaks = new List<(ChangeKind, long[])>();
        if (rule.Priority is { } priority) tweaks.Add((ChangeKind.Priority, [PriorityMapping.ToWin32(priority)]));
        var ids = Topology.BackgroundIds(rule.Cores);
        if (ids.Count > 0) tweaks.Add((ChangeKind.CpuSets, ids.Select(i => (long)i).ToArray()));
        if (rule.EcoQos) tweaks.Add((ChangeKind.PowerThrottling, [1, 1]));
        if (rule.LowIoPriority) tweaks.Add((ChangeKind.IoPriority, [1]));
        if (rule.LowMemoryPriority) tweaks.Add((ChangeKind.MemoryPriority, [2]));
        if (rule.HardWorkingSetCapMb > 0) tweaks.Add((ChangeKind.WorkingSetLimit, [rule.HardWorkingSetCapMb]));
        if (rule.SuspendDuringGame) tweaks.Add((ChangeKind.Suspended, [1]));

        var results = Apply(process, tweaks);
        if (results.Values.All(r => r == TweakResult.Gone)) return false;
        if (results.Values.Any(r => r is TweakResult.AccessDenied or TweakResult.Failed))
            Log.Warn($"{exe} (PID {pid}): {string.Join(", ", results.Where(r => r.Value != TweakResult.Ok).Select(r => $"{r.Key}={r.Value}"))}");

        _background[pid] = (rule, process);
        return true;
    }

    /// <summary>
    /// Lê os valores originais, grava todos no diário de uma vez e só então aplica.
    /// O que falhar sai do diário. Valores que já estão como queremos não entram no diário.
    /// </summary>
    private Dictionary<ChangeKind, TweakResult> Apply(ProcessRef process, List<(ChangeKind Kind, long[] Value)> tweaks)
    {
        var results = new Dictionary<ChangeKind, TweakResult>();
        var pending = new List<(JournalEntry Entry, long[] Value)>();

        foreach (var (kind, requested) in tweaks)
        {
            if (_journal.Has(process, kind)) { results[kind] = TweakResult.Ok; continue; }
            var read = _control.Read(process, kind, out var original);
            if (read != TweakResult.Ok) { results[kind] = read; continue; }

            var value = kind == ChangeKind.WorkingSetLimit ? WorkingSetCap(original, requested[0]) : requested;
            if (original.SequenceEqual(value)) { results[kind] = TweakResult.Ok; continue; }
            pending.Add((new JournalEntry
            {
                Kind = kind, Pid = process.Pid, ExeName = process.ExeName, StartTime = process.StartTime,
                Original = original, Applied = value,
            }, value));
        }

        _journal.AddRange(pending.Select(p => p.Entry).ToList());

        var failed = new HashSet<Guid>();
        foreach (var (entry, value) in pending)
        {
            var result = _control.Write(process, entry.Kind, value);
            results[entry.Kind] = result;
            if (result != TweakResult.Ok) failed.Add(entry.Id);
        }
        if (failed.Count > 0) _journal.RemoveWhere(e => failed.Contains(e.Id));
        return results;
    }

    /// <summary>Teto rígido: mantém o mínimo original (limitado à metade do teto) e liga só o limite de máximo.</summary>
    private static long[] WorkingSetCap(long[] original, long capMb)
    {
        var cap = capMb * 1024 * 1024;
        var min = Math.Min(original[0], cap / 2);
        return [min, cap, Native.Kernel32.QUOTA_LIMITS_HARDWS_MIN_DISABLE | Native.Kernel32.QUOTA_LIMITS_HARDWS_MAX_ENABLE];
    }

    // ---- memória ----

    private void GovernMemory()
    {
        var now = _time.GetUtcNow();
        foreach (var group in _background.Values.GroupBy(b => b.Rule).Where(g => g.Key.MemoryBudgetMb > 0))
        {
            var samples = group
                .Select(b => (b.Process, Bytes: _control.PrivateWorkingSet(b.Process) ?? 0))
                .Where(s => s.Bytes > 0)
                .ToList();
            var targets = MemoryGovernor.SelectTrimTargets(samples, group.Key.MemoryBudgetMb * 1024L * 1024)
                .Where(p => !_lastTrim.TryGetValue(p.Pid, out var last) || now - last >= TrimCooldown)
                .ToList();
            if (targets.Count == 0) continue;

            var trimmed = targets.Count(p => _control.TrimWorkingSet(p));
            foreach (var p in targets) _lastTrim[p.Pid] = now;
            _trimCount += trimmed;
            var total = samples.Sum(s => s.Bytes) / (1024 * 1024);
            Log.Info($"{group.Key.Name}: {total} MB > orçamento de {group.Key.MemoryBudgetMb} MB; {trimmed} processos esvaziados");
        }
    }

    private void PurgeStandbyIfNeeded(MemorySnapshot snapshot)
    {
        var now = _time.GetUtcNow();
        if (!StandbyPolicy.ShouldPurge(snapshot, _config.Memory, now, _lastPurge, _config.PurgeStandbyFor(Primary))) return;
        _lastPurge = now;
        if (_memory.PurgeStandbyList())
        {
            _purgeCount++;
            Log.Info($"Standby list limpa (livre {snapshot.FreeBytes >> 20} MB, standby {snapshot.StandbyBytes >> 20} MB)");
        }
        else
        {
            Log.Warn("Falha ao limpar a standby list (privilégio SeProfileSingleProcessPrivilege?)");
        }
    }

    private void RevertIfeo(JournalEntry entry)
    {
        try
        {
            _ifeo.Set(entry.ExeName, entry.Original.Length > 0 ? (int)entry.Original[0] : null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error($"Falha ao remover IFEO de {entry.ExeName}: {ex.Message}");
        }
    }

    private void Notify()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Log.Error($"Erro no StateChanged: {ex}"); }
    }
}
