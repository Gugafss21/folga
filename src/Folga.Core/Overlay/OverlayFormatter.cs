using System.Globalization;
using System.Text;

namespace Folga.Core.Overlay;

/// <summary>Os números do momento; null = ainda sem leitura (aparece "-").</summary>
public sealed record OverlayValues(
    double? Fps = null, double? OnePercentLow = null, double? FrameTimeMs = null,
    double? GpuPercent = null, double? VramGb = null, double? GpuTempC = null, double? PingMs = null,
    double? CpuPercent = null, double? RamFreeGb = null, int? Held = null);

/// <summary>Um pedaço de texto com cor (RRGGBB). Text = "\n" é quebra de linha.</summary>
public sealed record OverlaySegment(string Text, string Color);

/// <summary>
/// Monta o texto do overlay. Sai em pedaços com cor, que viram as marcações do RTSS
/// (<see cref="ToRtss"/>) ou a prévia da tela Overlay.
/// </summary>
public static class OverlayFormatter
{
    public const string Newline = "\n";
    private const string Missing = "-";
    private const string Separator = "   ";

    /// <summary>Exemplo para a prévia sem jogo aberto.</summary>
    public static OverlayValues Sample { get; } = new(141, 97, 7.1, 96, 4.8, 71, 34, 38, 3.2, 12);

    public static IReadOnlyList<OverlaySegment> Build(OverlaySettings settings, OverlayValues values)
    {
        var segments = new List<OverlaySegment>();
        foreach (var item in settings.Items.Where(i => i.Enabled))
        {
            if (segments.Count > 0)
                segments.Add(new OverlaySegment(settings.Layout == OverlayLayout.Lines ? Newline : Separator, settings.LabelColor));

            var label = item.Label.Trim();
            if (label.Length > 0) segments.Add(new OverlaySegment(label + " ", settings.LabelColor));

            var (value, unit) = Format(item.Metric, values);
            var warn = value is { } v && IsWarning(item, v);
            segments.Add(new OverlaySegment(value is { } x ? x.ToString(Decimals(item.Metric), CultureInfo.CurrentCulture) : Missing,
                warn ? settings.WarnColor : settings.ValueColor));
            if (unit.Length > 0) segments.Add(new OverlaySegment(unit, settings.LabelColor));
        }
        return segments;
    }

    /// <summary>Texto com as marcações do RTSS: canto (posição sticky + camada), tamanho e cor de cada pedaço.</summary>
    public static string ToRtss(OverlaySettings settings, IReadOnlyList<OverlaySegment> segments)
    {
        var sb = new StringBuilder();
        sb.Append($"<P{(int)settings.Corner}><L0>");
        var size = Math.Clamp(settings.SizePercent, 50, 200);
        if (size != 100) sb.Append($"<S={size}>");
        foreach (var s in segments)
        {
            // Tags são delimitadas por < >; o texto do usuário não pode abrir uma.
            var text = s.Text.Replace('<', '(').Replace('>', ')');
            sb.Append(s.Text == Newline ? Newline : $"<C={NormalizeColor(s.Color)}>{text}<C>");
        }
        if (size != 100) sb.Append("<S>");
        return sb.ToString();
    }

    /// <summary>RRGGBB em maiúsculas; inválido vira branco.</summary>
    public static string NormalizeColor(string? color)
    {
        var c = (color ?? "").Trim().TrimStart('#');
        return c.Length == 6 && c.All(Uri.IsHexDigit) ? c.ToUpperInvariant() : "FFFFFF";
    }

    /// <summary>FPS, 1% low e RAM livre alertam abaixo do limite; o resto, acima.</summary>
    public static bool LowIsBad(OverlayMetric metric) => metric is OverlayMetric.Fps or OverlayMetric.OnePercentLow or OverlayMetric.RamFree;

    private static bool IsWarning(OverlayItem item, double value) =>
        item.WarnAt is { } limit && (LowIsBad(item.Metric) ? value < limit : value > limit);

    private static (double? Value, string Unit) Format(OverlayMetric metric, OverlayValues v) => metric switch
    {
        OverlayMetric.Fps => (v.Fps, ""),
        OverlayMetric.OnePercentLow => (v.OnePercentLow, ""),
        OverlayMetric.FrameTime => (v.FrameTimeMs, " ms"),
        OverlayMetric.Gpu => (v.GpuPercent, "%"),
        OverlayMetric.Vram => (v.VramGb, " GB"),
        OverlayMetric.GpuTemp => (v.GpuTempC, "°C"),
        OverlayMetric.Ping => (v.PingMs, " ms"),
        OverlayMetric.Cpu => (v.CpuPercent, "%"),
        OverlayMetric.RamFree => (v.RamFreeGb, " GB"),
        OverlayMetric.Held => (v.Held, ""),
        _ => (null, ""),
    };

    private static string Decimals(OverlayMetric metric) =>
        metric is OverlayMetric.FrameTime or OverlayMetric.Vram or OverlayMetric.RamFree ? "0.0" : "0";
}
