using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Folga.App.Services;
using Folga.Core.Detection;
using Folga.Core.Processes;
using Folga.Core.Profiles;

namespace Folga.App.ViewModels;

public sealed partial class GameRowViewModel : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _executablesText = "";
    [ObservableProperty] private PriorityLevel _priority = PriorityLevel.High;
    [ObservableProperty] private GameCores _cores = GameCores.All;
    [ObservableProperty] private bool _useIfeoFallback = true;
    [ObservableProperty] private bool _markDscp;

    public bool AutoDetected { get; init; }
    public string Origin => AutoDetected ? "Detectado" : "Manual";

    public static GameRowViewModel From(GameProfile p) => new()
    {
        Enabled = p.Enabled, Name = p.Name, ExecutablesText = string.Join(", ", p.Executables),
        Priority = p.Priority, Cores = p.Cores, UseIfeoFallback = p.UseIfeoFallback, MarkDscp = p.MarkDscp,
        AutoDetected = p.AutoDetected,
    };

    public GameProfile ToModel() => new()
    {
        Enabled = Enabled, Name = Name.Trim(), Executables = SplitList(ExecutablesText),
        Priority = Priority, Cores = Cores, UseIfeoFallback = UseIfeoFallback, MarkDscp = MarkDscp,
        AutoDetected = AutoDetected,
    };

    public static List<string> SplitList(string text) =>
        text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public sealed record RunningProcess(int Pid, string ExeName, string Title, string? Path)
{
    public string Label => $"{Title}  ·  {ExeName}";
}

public sealed partial class JogosViewModel : ObservableObject
{
    private readonly AppServices _services;

    // .exe de jogos detectados que o usuário removeu: vão para as exclusões ao salvar, senão voltariam na próxima vez.
    private readonly List<string> _removedDetected = [];

    public JogosViewModel(AppServices services)
    {
        _services = services;
        Reload();
        services.Detector.Detected += game => Application.Current?.Dispatcher.BeginInvoke(() => OnDetected(game));
    }

    public ObservableCollection<GameRowViewModel> Rows { get; } = [];
    public ObservableCollection<RunningProcess> Running { get; } = [];

    [ObservableProperty] private GameRowViewModel? _selectedRow;
    [ObservableProperty] private RunningProcess? _selectedRunning;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _autoDetectEnabled;
    [ObservableProperty] private string _exclusionsText = "";

    private void Reload()
    {
        var config = _services.Engine.Config;
        Rows.Clear();
        foreach (var g in config.Games) Rows.Add(GameRowViewModel.From(g));
        AutoDetectEnabled = config.AutoDetect.Enabled;
        ExclusionsText = string.Join(", ", config.AutoDetect.Exclusions);
        _removedDetected.Clear();
    }

    /// <summary>Acrescenta a linha do jogo recém-detectado sem apagar o que estiver sendo editado.</summary>
    private void OnDetected(DetectedGame game)
    {
        if (Rows.Any(r => ExeMatcher.MatchesAny(GameRowViewModel.SplitList(r.ExecutablesText), game.ExeName))) return;
        if (_services.Engine.Config.Games.LastOrDefault(g => g.AutoDetected && g.Executables.Contains(game.ExeName, StringComparer.OrdinalIgnoreCase)) is not { } profile) return;
        Rows.Add(GameRowViewModel.From(profile));
        Status = $"{game.Name} detectado em tela cheia e adicionado.";
    }

    [RelayCommand]
    private void Add()
    {
        var row = new GameRowViewModel { Name = "Novo jogo", ExecutablesText = "jogo.exe" };
        Rows.Add(row);
        SelectedRow = row;
        Status = "Edite o nome e o .exe, depois salve.";
    }

    [RelayCommand]
    private void Remove()
    {
        if (SelectedRow is not { } row) return;
        Rows.Remove(row);
        if (row.AutoDetected)
        {
            _removedDetected.AddRange(GameRowViewModel.SplitList(row.ExecutablesText));
            Status = "Removido; ao salvar, vai para \"nunca tratar como jogo\".";
        }
        else Status = "Removido. Salve para aplicar.";
    }

    /// <summary>Lista apps com janela aberta: o jeito mais fácil de descobrir o .exe de um jogo.</summary>
    [RelayCommand]
    private async Task RefreshRunning()
    {
        var list = await Task.Run(() =>
        {
            var result = new List<RunningProcess>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle == 0 || string.IsNullOrWhiteSpace(p.MainWindowTitle) || p.Id == Environment.ProcessId) continue;
                        result.Add(new RunningProcess(p.Id, p.ProcessName + ".exe", p.MainWindowTitle, WindowsProcessControl.ImagePath(p.Id)));
                    }
                    catch (InvalidOperationException) { }
                }
            }
            return result.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        });
        Running.Clear();
        foreach (var r in list) Running.Add(r);
        SelectedRunning = Running.FirstOrDefault();
        Status = $"{Running.Count} apps com janela aberta.";
    }

    [RelayCommand]
    private void AddRunning()
    {
        if (SelectedRunning is not { } p) return;
        var row = new GameRowViewModel { Name = p.Title.Length <= 40 ? p.Title : p.ExeName, ExecutablesText = p.ExeName };
        Rows.Add(row);
        SelectedRow = row;
        Status = $"{p.ExeName} adicionado{(p.Path is null ? "" : $" ({p.Path})")}. Salve para aplicar.";
    }

    [RelayCommand]
    private void Save()
    {
        var games = Rows.Select(r => r.ToModel()).Where(g => g.Name.Length > 0 && g.Executables.Count > 0).ToList();
        var exclusions = GameRowViewModel.SplitList(ExclusionsText);
        foreach (var exe in _removedDetected)
        {
            if (!ExeMatcher.MatchesAny(exclusions, exe) && !ExeMatcher.MatchesAny(games.SelectMany(g => g.Executables), exe)) exclusions.Add(exe);
        }
        MainViewModel.Commit(_services, c =>
        {
            c.Games = games;
            c.AutoDetect.Enabled = AutoDetectEnabled;
            c.AutoDetect.Exclusions = exclusions;
        });
        Reload();
        Status = $"Salvo: {games.Count} jogos.";
    }
}
