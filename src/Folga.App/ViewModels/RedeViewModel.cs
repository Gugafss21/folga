using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Folga.App.Services;
using Folga.Core.Logging;
using Folga.Core.Network;

namespace Folga.App.ViewModels;

public sealed record NetworkRow(string Name, string Processes, string Download, string Upload, string Limit, bool IsGame, bool IsBusy);

public sealed partial class RedeViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, string?> _exeByPid = [];
    private EtwNetworkMonitor? _monitor;
    private IReadOnlyDictionary<int, NetworkCounters> _previous = new Dictionary<int, NetworkCounters>();
    private DateTime _previousAt;
    private int _busy;

    public RedeViewModel(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
        Reload();
    }

    public ObservableCollection<NetworkRow> Rows { get; } = [];

    [ObservableProperty] private bool _pauseWindowsUpdate;
    [ObservableProperty] private string _monitorStatus = "";
    [ObservableProperty] private string _sessionSummary = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isVisible;

    /// <summary>O monitor gera um evento por pacote: só fica ligado com a tela aberta.</summary>
    partial void OnIsVisibleChanged(bool value)
    {
        if (value)
        {
            Reload(); // as regras podem ter mudado em outra tela
            StartMonitor();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _monitor?.Dispose();
            _monitor = null;
        }
    }

    private void Reload()
    {
        var config = _services.Engine.Config;
        PauseWindowsUpdate = config.Network.PauseWindowsUpdate;
        var limited = config.Background.Count(r => r.Enabled && r.UploadLimitKbps > 0);
        var marked = config.Games.Count(g => g.Enabled && g.MarkDscp);
        var parts = new List<string> { $"upload limitado em {limited} apps" };
        if (config.Network.PauseWindowsUpdate) parts.Add("Windows Update pausado");
        if (marked > 0) parts.Add($"tráfego de {marked} jogo(s) marcado com DSCP 46");
        SessionSummary = "Durante o jogo: " + string.Join(", ", parts) + ".";
    }

    private void StartMonitor()
    {
        try
        {
            _monitor = new EtwNetworkMonitor();
            _monitor.Start();
            _previous = _monitor.Snapshot();
            _previousAt = DateTime.UtcNow;
            MonitorStatus = "Medindo ao vivo, a cada segundo.";
        }
        catch (Exception ex)
        {
            _monitor?.Dispose();
            _monitor = null;
            MonitorStatus = ex is UnauthorizedAccessException
                ? "Para ver quem usa a rede, abra o Folga como administrador."
                : $"Monitor de rede indisponível: {ex.Message}";
            Log.Warn($"Monitor de rede: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Save()
    {
        MainViewModel.Commit(_services, c => c.Network.PauseWindowsUpdate = PauseWindowsUpdate);
        Reload();
        Status = "Salvo.";
    }

    private async void Refresh()
    {
        if (_monitor is null || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var config = _services.Engine.Config;
            var monitor = _monitor;
            var rows = await Task.Run(() =>
            {
                var now = DateTime.UtcNow;
                var current = monitor.Snapshot();
                var rates = NetworkRates.Compute(_previous, current, now - _previousAt, ExeOf);
                _previous = current;
                _previousAt = now;
                return rates.Take(25).Select(r =>
                {
                    var rule = config.FindRule(r.ExeName);
                    var limit = rule is { UploadLimitKbps: > 0 } ? $"↑ {FormatKbps(rule.UploadLimitKbps)}" : "—";
                    var total = r.DownloadBytesPerSecond + r.UploadBytesPerSecond;
                    return new NetworkRow(r.ExeName, r.Processes.ToString(CultureInfo.CurrentCulture),
                        FormatRate(r.DownloadBytesPerSecond), FormatRate(r.UploadBytesPerSecond), limit,
                        config.FindGame(r.ExeName) is not null, total >= 1024 * 1024);
                }).ToList();
            });
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private string? ExeOf(int pid)
    {
        if (_exeByPid.TryGetValue(pid, out var name)) return name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName + ".exe";
        }
        catch (ArgumentException) { name = null; } // já saiu
        catch (InvalidOperationException) { name = null; }
        if (_exeByPid.Count > 4096) _exeByPid.Clear();
        _exeByPid[pid] = name;
        return name;
    }

    private static string FormatRate(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => bytesPerSecond < 1 ? "—" : $"{bytesPerSecond:0} B/s",
        < 1024 * 1024 => (bytesPerSecond / 1024).ToString("0", CultureInfo.CurrentCulture) + " KB/s",
        _ => (bytesPerSecond / 1024 / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " MB/s",
    };

    private static string FormatKbps(int kbps) =>
        kbps >= 1000 ? (kbps / 1000d).ToString("0.#", CultureInfo.CurrentCulture) + " Mbit/s" : $"{kbps} kbit/s";
}
