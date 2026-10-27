using Folga.Core.Engine;
using Folga.Core.Logging;
using Folga.Core.Memory;
using Folga.Core.Native;
using Folga.Core.Overlay;
using Folga.Core.Telemetry;

namespace Folga.App.Services;

/// <summary>Situação do overlay para a tela: o RTSS está aberto? O texto do Folga está no ar?</summary>
public sealed record OverlayState(RtssStatus Rtss, bool Showing);

/// <summary>
/// A cada segundo escreve os números do jogo no slot do Folga no RTSS. Quando o overlay não deve
/// aparecer (desligado, ou sem jogo com "só durante o jogo"), libera o slot e o texto some.
/// </summary>
public sealed class OverlayService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private const double Gb = 1024d * 1024 * 1024;

    private readonly object _gate = new();
    private readonly BoostEngine _engine;
    private readonly TelemetryService _telemetry;
    private readonly ISystemMemory _memory;
    private readonly RtssOsd _osd = new();
    private PdhCounterArray? _cpu; // só criado se o item CPU estiver ligado
    private Timer? _timer;
    private int _busy;
    private bool _showing, _disposed;

    public OverlayService(BoostEngine engine, TelemetryService telemetry, ISystemMemory memory)
    {
        _engine = engine;
        _telemetry = telemetry;
        _memory = memory;
    }

    public OverlayState State { get; private set; } = new(RtssStatus.NotRunning, false);

    /// <summary>Disparado na thread do timer quando o estado muda.</summary>
    public event Action<OverlayState>? StateChanged;

    public void Start() => _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, Interval);

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            OverlayState state;
            lock (_gate)
            {
                if (_disposed) return;
                state = Update();
            }
            if (state == State) return;
            if (state.Rtss != State.Rtss) Log.Info($"Overlay: RTSS {Describe(state.Rtss)}");
            State = state;
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            Log.Error($"Erro no overlay: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private OverlayState Update()
    {
        var settings = _engine.Config.Overlay;
        var engine = _engine.GetState();
        if (!settings.Enabled || (settings.OnlyDuringGame && !engine.SessionActive))
        {
            if (_showing) _osd.Release();
            _showing = false;
            return new OverlayState(_osd.Check(), false);
        }

        var text = OverlayFormatter.ToRtss(settings, OverlayFormatter.Build(settings, Values(settings, engine)));
        var status = _osd.Update(text);
        _showing = status is RtssStatus.Ok or RtssStatus.Busy;
        return new OverlayState(status == RtssStatus.Busy ? RtssStatus.Ok : status, _showing);
    }

    private OverlayValues Values(OverlaySettings settings, EngineState engine)
    {
        bool On(OverlayMetric m) => settings.Items.Any(i => i.Enabled && i.Metric == m);

        var t = _telemetry.Snapshot();
        return new OverlayValues(
            t.Fps, t.OnePercentLow, t.FrameTimeMs,
            t.Gpu?.UsagePercent, t.Gpu?.VramUsedBytes / Gb, t.Gpu?.TemperatureC, t.Ping?.RttMs,
            On(OverlayMetric.Cpu) ? CpuTotal() : null,
            On(OverlayMetric.RamFree) ? _memory.Read().FreeBytes / Gb : null,
            engine.SessionActive ? engine.HeldProcesses : null);
    }

    private double? CpuTotal()
    {
        _cpu ??= new PdhCounterArray(@"\Processor Information(*)\% Processor Utility");
        return _cpu.Read()?.FirstOrDefault(v => v.Instance == "_Total") is { Instance: not null } total ? Math.Clamp(total.Value, 0, 100) : null;
    }

    public static string Describe(RtssStatus status) => status switch
    {
        RtssStatus.Ok or RtssStatus.Busy => "aberto",
        RtssStatus.NotRunning => "fechado",
        RtssStatus.NoFreeSlot => "sem slot livre (outros apps ocupam todos)",
        RtssStatus.TooOld => "antigo demais (precisa da versão 7 ou mais nova)",
        _ => status.ToString(),
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            if (_showing) _osd.Release(); // o texto não fica parado na tela depois que o Folga fecha
            _cpu?.Dispose();
        }
    }
}
