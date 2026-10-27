using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Folga.Core.Telemetry;

namespace Folga.App.Views;

/// <summary>
/// Frametime dos últimos 60 s. Cada coluna de pixel mostra o pior quadro do seu trecho, para nenhuma
/// travada sumir na redução. Linhas-guia em 16,7 ms (60 FPS) e 33,3 ms (30 FPS).
/// </summary>
public sealed class FrameTimeChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<FrameSample>), typeof(FrameTimeChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<FrameSample>? Samples
    {
        get => (IReadOnlyList<FrameSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    private static readonly (double Ms, string Label)[] Guides = [(1000 / 60d, "60 FPS"), (1000 / 30d, "30 FPS")];

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 10 || height < 10) return;

        var line = (Brush)FindResource("AccentBrush");
        var grid = (Brush)FindResource("LineBrush");
        var label = (Brush)FindResource("TextTertiaryBrush");
        var samples = Samples;

        // Escala: pelo menos até 30 FPS; acima disso acompanha as travadas, com teto de 100 ms.
        var worst = samples is { Count: > 0 } ? samples.Max(s => s.FrameTimeMs) : 0;
        var yMax = Math.Clamp(Math.Max(1000 / 30d * 1.2, worst * 1.1), 40, 100);
        double Y(double ms) => height - Math.Min(ms, yMax) / yMax * height;

        var gridPen = new Pen(grid, 1) { DashStyle = new DashStyle([4, 4], 0) };
        gridPen.Freeze();
        foreach (var (ms, text) in Guides)
        {
            var y = Math.Round(Y(ms)) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
            dc.DrawText(Text(text, label), new Point(4, y - 15));
        }

        if (samples is not { Count: > 1 }) return;

        var end = samples[^1].AtMs;
        var start = end - FrameStats.HistoryMs;
        var columns = (int)width;
        var peak = new double[columns];
        foreach (var s in samples)
        {
            var col = (int)((s.AtMs - start) / FrameStats.HistoryMs * (columns - 1));
            if (col >= 0 && col < columns && s.FrameTimeMs > peak[col]) peak[col] = s.FrameTimeMs;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var started = false;
            for (var x = 0; x < columns; x++)
            {
                if (peak[x] <= 0) continue;
                var point = new Point(x, Y(peak[x]));
                if (!started) { ctx.BeginFigure(point, false, false); started = true; }
                else ctx.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        var pen = new Pen(line, 1.5) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private FormattedText Text(string text, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
