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

/// <summary>Uma causa no diagnóstico das travadas: quanto do tempo, quanto das travadas e quantas vezes mais trava.</summary>
public sealed record CauseRow(string Name, string Hint, string Time, string Share, string Ratio, string Values, bool Likely);

public sealed record DiagnosisCard(string Game, string Verdict, string? Advice, IReadOnlyList<CauseRow> Rows, string Footer);

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
    public ObservableCollection<DiagnosisCard> Diagnoses { get; } = [];
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
        Diagnoses.Clear();
        foreach (var game in records.Where(r => r.HitchFactors is { Count: > 0 }).GroupBy(r => r.Game, StringComparer.OrdinalIgnoreCase))
            Diagnoses.Add(Diagnosis(game.Key, game.ToList()));
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

    /// <summary>Nome, explicação, unidade, conversão do valor guardado e o que fazer, por causa.</summary>
    private sealed record CauseText(string Name, string Hint, string Unit, Func<double, double> Scale, string Format, string Advice);

    private static readonly Dictionary<HitchCause, CauseText> Causes = new()
    {
        [HitchCause.GameDiskRead] = new("Jogo lendo do disco",
            $"Segundos em que os processos do jogo leram mais de {HitchProfiler.DiskThresholdMBps:0} MB/s do disco: carregando área nova, texturas ou mapa (streaming). Os números da direita são a leitura média na hora das travadas e no resto do tempo.",
            "MB/s", v => v, "0",
            "O jogo trava quando carrega dados do disco. Ajuda: instalar o jogo num SSD (de preferência NVMe), baixar Texturas ou a distância de visão um nível, e não deixar Steam, OneDrive ou antivírus mexendo no mesmo disco durante a partida."),
        [HitchCause.HardFaults] = new("Windows buscando memória no disco",
            $"Segundos em que o Windows leu mais de {HitchProfiler.HardFaultThresholdMBps:0} MB/s do disco para trazer de volta à RAM partes de programas que tinham ido para o arquivo de paginação (falhas de página graves, do PC inteiro). É o sinal mais direto de RAM curta.",
            "MB/s", v => v, "0",
            "Falta RAM: o Windows tira partes do jogo da memória e precisa buscá-las de volta no disco. Ajuda: fechar o navegador ou deixar poucas abas, dar um orçamento de RAM ao navegador na tela Segundo plano (coluna RAM MB) e ligar a limpeza da standby list ali mesmo."),
        [HitchCause.LowRam] = new("Pouca RAM disponível",
            "Segundos com menos de 10% da RAM do PC disponível (no mínimo 1 GB). Disponível = livre + cache que o Windows pode reaproveitar na hora. Os números da direita são a RAM disponível média na hora das travadas e no resto do tempo.",
            "GB", v => v / 1024, "0.0",
            "A RAM está quase toda ocupada quando trava. Ajuda: fechar o navegador ou deixar poucas abas e dar um orçamento de RAM aos apps de fundo na tela Segundo plano (coluna RAM MB)."),
        [HitchCause.VramFull] = new("Memória da placa de vídeo cheia",
            $"Segundos com a memória da placa de vídeo (VRAM) acima de {HitchProfiler.VramThresholdPercent:0}% ocupada, lida pelo driver. Inclui o que outros programas usam da placa, como navegador e Discord.",
            "%", v => v, "0",
            "A memória da placa de vídeo está cheia e as texturas transbordam para a RAM, o que trava. Ajuda: baixar Texturas um nível no jogo (é a opção que mais usa VRAM) e fechar o que usa a placa junto, como vídeo no navegador ou Discord com aceleração de hardware."),
        [HitchCause.CpuFull] = new("Processador no limite",
            $"Segundos com o processador inteiro (todas as threads) acima de {HitchProfiler.CpuThresholdPercent:0}% de uso.",
            "%", v => v, "0",
            "O processador está no limite quando trava. Ajuda: limitar o FPS um pouco abaixo da média (no jogo ou no Painel de Controle da NVIDIA). Sobra folga para os picos e os quadros ficam mais regulares."),
        [HitchCause.OtherCpu] = new("Outros programas usando CPU",
            $"Segundos em que os programas que não são o jogo usaram, somados, mais de {HitchProfiler.OtherCpuThresholdPercent:0}% do processador inteiro.",
            "%", v => v, "0",
            "Outros programas disputam o processador nos momentos de travada. Ajuda: na tela Segundo plano, segure ou congele os apps que aparecem usando CPU durante o jogo. Atualizações (Windows, Steam, navegador) são as suspeitas comuns."),
        [HitchCause.AfterLoading] = new("Logo depois de carregar",
            "O primeiro minuto da partida e o primeiro minuto depois de cada carregamento (o jogo ficou mais de 2 s sem desenhar).",
            "", v => v, "0",
            "As travadas se concentram logo depois de carregar: é o jogo compilando shaders ou montando o mapa. Costuma melhorar nas partidas seguintes no mesmo mapa. Ajuda: no Painel de Controle da NVIDIA, deixar o Tamanho do cache do sombreador em 10 GB ou Ilimitado, e não limpar o cache do driver."),
    };

    private static DiagnosisCard Diagnosis(string game, IReadOnlyList<SessionRecord> sessions)
    {
        var factors = HitchVerdict.Combine(sessions.Select(s => s.HitchFactors));
        var hitches = factors.Max(f => f.Hitches);
        var minutes = factors.Max(f => f.Seconds) / 60.0;
        var likely = HitchVerdict.Likely(factors);

        string verdict;
        string? advice = null;
        if (hitches < HitchVerdict.MinHitches)
            verdict = $"Poucas travadas até agora ({hitches}) para apontar uma causa. Jogue mais partidas: o diagnóstico junta todas as deste jogo.";
        else if (likely.Count == 0)
            verdict = "As travadas não se concentram em nenhuma das situações medidas. Podem vir do próprio jogo (servidor, física, áudio) ou de algo que o Folga não mede, como driver.";
        else
        {
            var top = likely[0];
            verdict = $"Causa provável: {Lower(Causes[top.Cause].Name)}. {Percent(top.ShareOfHitches)} das travadas aconteceram nesses momentos, que são {Percent(top.ShareOfTime)} do tempo de jogo ({(top.Ratio is double.PositiveInfinity ? "nenhuma travada fora deles" : $"{RatioText(top.Ratio)} mais travadas que no resto")})."
                + (likely.Count > 1 ? $" Também pesa: {string.Join(", ", likely.Skip(1).Select(f => Lower(Causes[f.Cause].Name)))}." : "");
            advice = Causes[top.Cause].Advice;
        }

        var rows = factors
            .OrderByDescending(f => HitchVerdict.IsLikely(f)).ThenByDescending(f => f.ShareOfHitches)
            .Select(f =>
            {
                var text = Causes[f.Cause];
                string Value(double? v) => v is { } x ? text.Scale(x).ToString(text.Format, CultureInfo.CurrentCulture) : "—";
                var values = text.Unit.Length == 0 ? "" : $"{Value(f.AverageAtHitch)} · {Value(f.AverageCalm)} {text.Unit}";
                return new CauseRow(text.Name, text.Hint, Percent(f.ShareOfTime), Percent(f.ShareOfHitches), RatioText(f.Ratio), values, HitchVerdict.IsLikely(f));
            })
            .ToList();
        var footer = $"{sessions.Count} {(sessions.Count == 1 ? "partida" : "partidas")} ({Duration(minutes)} jogando)  ·  {hitches} {(hitches == 1 ? "travada" : "travadas")}";
        return new DiagnosisCard(game, verdict, advice, rows, footer);
    }

    /// <summary>Nome no meio da frase: só a primeira letra muda, e "Windows" fica como está.</summary>
    private static string Lower(string name) => name.StartsWith("Windows", StringComparison.Ordinal) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static string Percent(double share) => (share * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    private static string RatioText(double? ratio) => ratio switch
    {
        null => "—",
        double.PositiveInfinity => "só nela",
        { } r => r.ToString(r < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + "×",
    };

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
