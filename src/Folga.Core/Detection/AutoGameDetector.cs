using Folga.Core.Logging;
using Folga.Core.Profiles;

namespace Folga.Core.Detection;

/// <summary>
/// A janela em primeiro plano. <c>Fullscreen</c>: cobre o monitor inteiro e não tem barra de título
/// (tela cheia exclusiva ou janela sem borda; uma janela maximizada comum não conta).
/// <c>IsSystem</c>: o .exe fica na pasta do Windows.
/// </summary>
public sealed record ForegroundWindow(int Pid, string ExeName, string Title, bool Fullscreen, bool IsSystem);

public interface IForegroundSource
{
    ForegroundWindow? Read();
}

/// <summary>Mede o FPS de um processo pelo DirectX enquanto ele é candidato a jogo.</summary>
public interface IPresentProbe : IDisposable
{
    /// <summary>FPS do último segundo; 0 se não desenhou nada.</summary>
    double Fps { get; }
}

public sealed record DetectedGame(int Pid, string ExeName, string Name, double? Fps);

/// <summary>
/// Descobre jogos que não estão na lista: o app em primeiro plano, em tela cheia, que fica assim por
/// <see cref="AutoDetectSettings.ConfirmSeconds"/> e desenha com DirectX a pelo menos
/// <see cref="AutoDetectSettings.MinFps"/>. Sem admin não há como medir o DirectX, e aí valem só a tela
/// cheia e as exclusões. Só procura quando não há jogo aberto.
/// </summary>
public sealed class AutoGameDetector : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private const int GiveUpFactor = 3; // sem DirectX depois de 3x o tempo de confirmação: não é jogo
    private const int MaxNameLength = 40;

    private readonly object _gate = new();
    private readonly IForegroundSource _foreground;
    private readonly Func<int, IPresentProbe?> _probeFactory;
    private readonly Func<FolgaConfig> _config;
    private readonly Func<bool> _sessionActive;
    private readonly TimeProvider _time;
    private readonly HashSet<(int Pid, string Exe)> _settled = []; // já decididos: viraram jogo ou foram descartados
    private (ForegroundWindow Window, DateTimeOffset Since, IPresentProbe? Probe)? _candidate;
    private ITimer? _timer;
    private bool _disposed;

    /// <param name="probeFactory">Liga a medição de DirectX para um PID; null se não der (sem admin).</param>
    public AutoGameDetector(IForegroundSource foreground, Func<int, IPresentProbe?> probeFactory,
        Func<FolgaConfig> config, Func<bool> sessionActive, TimeProvider? time = null)
    {
        _foreground = foreground;
        _probeFactory = probeFactory;
        _config = config;
        _sessionActive = sessionActive;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Disparado fora da trava, na thread do timer.</summary>
    public event Action<DetectedGame>? Detected;

    public void Start() => _timer = _time.CreateTimer(_ => Tick(), null, Interval, Interval);

    /// <summary>Uma verificação (o timer chama a cada segundo; os testes também).</summary>
    public void Tick()
    {
        DetectedGame? detected;
        lock (_gate)
        {
            if (_disposed) return;
            detected = Evaluate();
        }
        if (detected is null) return;
        try { Detected?.Invoke(detected); }
        catch (Exception ex) { Log.Error($"Erro tratando jogo detectado: {ex}"); }
    }

    private DetectedGame? Evaluate()
    {
        var config = _config();
        if (!config.BoostEnabled || !config.AutoDetect.Enabled || _sessionActive()) return Reset();

        ForegroundWindow? window;
        try { window = _foreground.Read(); }
        catch (Exception ex) { Log.Error($"Erro lendo a janela em primeiro plano: {ex.Message}"); return Reset(); }

        if (window is null || !window.Fullscreen || window.IsSystem || window.Pid == Environment.ProcessId
            || config.IsKnownGame(window.ExeName) || config.IsExcludedFromAutoDetect(window.ExeName)
            || _settled.Contains((window.Pid, window.ExeName)))
            return Reset();

        var now = _time.GetUtcNow();
        if (_candidate is not { } candidate || candidate.Window.Pid != window.Pid)
        {
            Reset();
            _candidate = (window, now, _probeFactory(window.Pid));
            return null;
        }

        var elapsed = now - candidate.Since;
        var confirm = TimeSpan.FromSeconds(Math.Max(1, config.AutoDetect.ConfirmSeconds));
        if (elapsed < confirm) return null;

        double? fps = candidate.Probe?.Fps;
        if (fps < config.AutoDetect.MinFps)
        {
            if (elapsed < confirm * GiveUpFactor) return null; // pode estar numa tela de carregamento
            Log.Info($"{window.ExeName} em tela cheia, mas sem desenhar como jogo ({fps:0} FPS); não é jogo");
            _settled.Add((window.Pid, window.ExeName));
            return Reset();
        }

        _settled.Add((window.Pid, window.ExeName));
        Reset();
        return new DetectedGame(window.Pid, window.ExeName, GameName(window), fps);
    }

    /// <summary>O título da janela costuma ser o nome do jogo; se for vazio ou comprido demais, o .exe.</summary>
    internal static string GameName(ForegroundWindow window)
    {
        var title = window.Title.Trim();
        return title.Length is > 0 and <= MaxNameLength ? title : Path.GetFileNameWithoutExtension(window.ExeName);
    }

    private DetectedGame? Reset()
    {
        _candidate?.Probe?.Dispose();
        _candidate = null;
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            Reset();
        }
    }
}
