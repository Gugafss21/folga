using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Folga.App.Services;
using Folga.Core.Diagnostics;
using Folga.Core.Profiles;

namespace Folga.App.ViewModels;

public sealed partial class RuleRowViewModel : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _executablesText = "";
    [ObservableProperty] private PriorityChoice _priority = PriorityChoice.BelowNormal;
    [ObservableProperty] private BackgroundCores _cores = BackgroundCores.Auto;
    [ObservableProperty] private bool _ecoQos = true;
    [ObservableProperty] private bool _lowIoPriority = true;
    [ObservableProperty] private bool _lowMemoryPriority = true;
    [ObservableProperty] private int _memoryBudgetMb;
    [ObservableProperty] private int _hardWorkingSetCapMb;

    public static RuleRowViewModel From(BackgroundRule r) => new()
    {
        Enabled = r.Enabled, Name = r.Name, ExecutablesText = string.Join(", ", r.Executables),
        Priority = Choices.From(r.Priority), Cores = r.Cores, EcoQos = r.EcoQos, LowIoPriority = r.LowIoPriority,
        LowMemoryPriority = r.LowMemoryPriority, MemoryBudgetMb = r.MemoryBudgetMb, HardWorkingSetCapMb = r.HardWorkingSetCapMb,
    };

    public BackgroundRule ToModel() => new()
    {
        Enabled = Enabled, Name = Name.Trim(), Executables = GameRowViewModel.SplitList(ExecutablesText),
        Priority = Choices.ToLevel(Priority), Cores = Cores, EcoQos = EcoQos, LowIoPriority = LowIoPriority,
        LowMemoryPriority = LowMemoryPriority, MemoryBudgetMb = Math.Max(0, MemoryBudgetMb),
        HardWorkingSetCapMb = Math.Max(0, HardWorkingSetCapMb),
    };
}

public sealed record GroupRow(string Name, string Processes, string Memory, bool OverBudget, string Priority, string EcoQos, string Cores, string MemoryPriority);

public sealed partial class SegundoPlanoViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly BackgroundInspector _inspector;
    private readonly DispatcherTimer _timer;
    private int _busy;

    public SegundoPlanoViewModel(AppServices services)
    {
        _services = services;
        _inspector = new BackgroundInspector(services.Control);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => RefreshGroups(), Dispatcher.CurrentDispatcher);
        Reload();
    }

    public ObservableCollection<RuleRowViewModel> Rules { get; } = [];
    public ObservableCollection<GroupRow> Groups { get; } = [];

    [ObservableProperty] private RuleRowViewModel? _selectedRule;
    [ObservableProperty] private bool _purgeStandbyList;
    [ObservableProperty] private int _freeThresholdMb;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isVisible;

    partial void OnIsVisibleChanged(bool value)
    {
        if (value) { RefreshGroups(); _timer.Start(); }
        else _timer.Stop();
    }

    private void Reload()
    {
        var config = _services.Engine.Config;
        Rules.Clear();
        foreach (var r in config.Background) Rules.Add(RuleRowViewModel.From(r));
        PurgeStandbyList = config.Memory.PurgeStandbyList;
        FreeThresholdMb = config.Memory.FreeThresholdMb;
    }

    [RelayCommand]
    private void Add()
    {
        var row = new RuleRowViewModel { Name = "Novo app", ExecutablesText = "app.exe" };
        Rules.Add(row);
        SelectedRule = row;
        Status = "Edite o nome e o .exe, depois salve.";
    }

    [RelayCommand]
    private void Remove()
    {
        if (SelectedRule is null) return;
        Rules.Remove(SelectedRule);
        Status = "Removido. Salve para aplicar.";
    }

    [RelayCommand]
    private void Save()
    {
        var rules = Rules.Select(r => r.ToModel()).Where(r => r.Name.Length > 0 && r.Executables.Count > 0).ToList();
        MainViewModel.Commit(_services, c =>
        {
            c.Background = rules;
            c.Memory.PurgeStandbyList = PurgeStandbyList;
            c.Memory.FreeThresholdMb = Math.Max(256, FreeThresholdMb);
        });
        Reload();
        Status = $"Salvo: {rules.Count} regras.";
        RefreshGroups();
    }

    private async void RefreshGroups()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var config = _services.Engine.Config;
            var groups = await Task.Run(() => _inspector.Inspect(config));
            Groups.Clear();
            foreach (var g in groups) Groups.Add(ToRow(g));
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static GroupRow ToRow(BackgroundGroupStatus g)
    {
        var mb = g.WorkingSetBytes / (1024 * 1024);
        var memory = g.ProcessCount == 0 ? "—"
            : g.BudgetMb > 0 ? $"{mb.ToString("N0", CultureInfo.CurrentCulture)} / {g.BudgetMb.ToString("N0", CultureInfo.CurrentCulture)} MB"
            : $"{mb.ToString("N0", CultureInfo.CurrentCulture)} MB";
        return new GroupRow(g.RuleName, g.ProcessCount == 0 ? "fechado" : g.ProcessCount.ToString(CultureInfo.CurrentCulture),
            memory, g.BudgetMb > 0 && mb > g.BudgetMb, g.Priority, g.EcoQos, g.Cores, g.MemoryPriority);
    }
}
