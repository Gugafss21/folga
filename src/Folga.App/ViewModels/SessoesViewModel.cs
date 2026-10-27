using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Folga.App.Services;
using Folga.Core.Telemetry;

namespace Folga.App.ViewModels;

/// <summary>Uma linha da comparação: o número com boost, sem boost e a diferença (Better decide a cor).</summary>
public sealed record MetricRow(string Name, string Hint, string Boost, string Plain, string Difference, bool? Better);

public sealed record ComparisonCard(string Game, IReadOnlyList<MetricRow> Rows, string Footer, string? Missing);

public sealed record SessionRow(Guid Id, string When, string Game, string Mode, string Duration, string Fps, string Low, string PointOneLow,
    string Hitches, string Ping, string Gpu);

public sealed partial class SessoesViewModel : ObservableObject
{
    /// <summary>Abaixo disso a diferença é ruído normal entre partidas (mapa, servidor, quantos jogadores).</summary>
    private const double NoisePercent = 5;

    private readonly AppServices _services;
    private bool _syncing;

    public SessoesViewModel(AppServices services)
    {
        _services = services;
        services.Sessions.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Reload);
        services.Engine.StateChanged += () => Application.Current?.Dispatcher.BeginInvoke(SyncMeasureOnly);
        SyncMeasureOnly();
        Reload();
    }

    public ObservableCollection<ComparisonCard> Cards { get; } = [];
    public ObservableCollection<SessionRow> Sessions { get; } = [];

    [ObservableProperty] private bool _measureOnly;
    [ObservableProperty] private SessionRow? _selectedSession;
    [ObservableProperty] private string _status = "";

    partial void OnMeasureOnlyChanged(bool value)
    {
        if (_syncing) return;
        _services.Engine.SetMeasureOnly(value);
        _services.SaveEngineConfig();
        Status = value ? "Só medir ligado: as próximas partidas são medidas sem boost." : "Boost normal nas próximas partidas.";
    }

    private void SyncMeasureOnly()
    {
        _syncing = true;
        MeasureOnly = _services.Engine.GetState().MeasureOnly;
        _syncing = false;
    }

    private void Reload()
    {
        var records = _services.Sessions.All;
        Cards.Clear();
        foreach (var c in SessionLog.Compare(records)) Cards.Add(Card(c));
        Sessions.Clear();
        foreach (var r in records.OrderByDescending(r => r.Start)) Sessions.Add(Row(r));
    }

    private static ComparisonCard Card(SessionComparison c)
    {
        var b = c.Boost;
        var p = c.Plain;
        var rows = new List<MetricRow>
        {
            Metric("FPS médio", "Quadros por segundo na partida inteira. Mais é melhor, mas é o número que o Folga menos muda.",
                b?.AverageFps, p?.AverageFps, higherIsBetter: true, "0"),
            Metric("1% low", "FPS dos 1% piores quadros da partida: as travadinhas. É onde o Folga deve fazer diferença com o navegador aberto. Mais é melhor.",
                b?.OnePercentLow, p?.OnePercentLow, higherIsBetter: true, "0"),
            Metric("0,1% low", "FPS dos 0,1% piores quadros: as travadas mais fortes. Varia bastante entre partidas; olhe junto com o 1% low. Mais é melhor.",
                b?.PointOnePercentLow, p?.PointOnePercentLow, higherIsBetter: true, "0"),
            Metric("Travadas por minuto", "Quadros acima de 50 ms (abaixo de 20 FPS naquele instante), que dá para sentir na mão. Menos é melhor.",
                b?.HitchesPerMinute, p?.HitchesPerMinute, higherIsBetter: false, "0.0"),
            Metric("Ping médio (ms)", "Ping médio até o servidor da partida. Depende muito do servidor sorteado; diferenças de poucos ms são normais. Menos é melhor.",
                b?.PingAverageMs, p?.PingAverageMs, higherIsBetter: false, "0"),
        };
        var footer = $"Com boost: {Count(b)}  ·  Sem boost: {Count(p)}";
        var missing = b is null ? "Ainda sem partidas com boost deste jogo. Desligue o Só medir e jogue algumas partidas."
            : p is null ? "Ainda sem partidas sem boost deste jogo. Ligue o Só medir acima e jogue algumas partidas para comparar."
            : null;
        return new ComparisonCard(c.Game, rows, footer, missing);
    }

    private static MetricRow Metric(string name, string hint, double? boost, double? plain, bool higherIsBetter, string format)
    {
        string Text(double? v) => v is { } x ? x.ToString(format, CultureInfo.CurrentCulture) : "—";
        if (boost is not { } b || plain is not { } p || p == 0) return new MetricRow(name, hint, Text(boost), Text(plain), "", null);

        var percent = (b - p) / p * 100;
        bool? better = Math.Abs(percent) < NoisePercent ? null : (percent > 0) == higherIsBetter;
        var diff = (percent >= 0 ? "+" : "") + percent.ToString("0", CultureInfo.CurrentCulture) + "%";
        return new MetricRow(name, hint, Text(boost), Text(plain), diff, better);
    }

    private static string Count(SessionAverage? a) =>
        a is null ? "nenhuma" : $"{a.Sessions} {(a.Sessions == 1 ? "partida" : "partidas")} ({Duration(a.Minutes)})";

    private static SessionRow Row(SessionRecord r) => new(
        r.Id,
        r.Start.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture),
        r.Game,
        r.Boost ? "Com boost" : "Sem boost",
        Duration(r.Minutes),
        N(r.AverageFps, "0"), N(r.OnePercentLow, "0"), N(r.PointOnePercentLow, "0"),
        r.HitchesPerMinute.ToString("0.0", CultureInfo.CurrentCulture),
        N(r.PingAverageMs, "0"), r.GpuAveragePercent is { } g ? g.ToString("0", CultureInfo.CurrentCulture) + "%" : "—");

    private static string N(double? v, string format) => v is { } x ? x.ToString(format, CultureInfo.CurrentCulture) : "—";

    private static string Duration(double minutes) =>
        minutes >= 60 ? $"{(int)(minutes / 60)} h {(int)(minutes % 60):00} min" : $"{minutes:0} min";

    [RelayCommand]
    private void Remove()
    {
        if (SelectedSession is not { } row) return;
        _services.Sessions.Remove(row.Id);
        Status = "Partida removida da comparação.";
    }
}
