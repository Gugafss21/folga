using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Folga.App.ViewModels;
using Folga.Core.Overlay;

namespace Folga.App.Views;

/// <summary>Prévia do overlay: os mesmos pedaços com cor que vão para o RTSS, desenhados em texto.</summary>
public sealed class OverlayPreview : TextBlock
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<OverlaySegment>), typeof(OverlayPreview),
        new PropertyMetadata(null, (d, _) => ((OverlayPreview)d).Rebuild()));

    public IReadOnlyList<OverlaySegment>? Segments
    {
        get => (IReadOnlyList<OverlaySegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();
        foreach (var s in Segments ?? [])
        {
            if (s.Text == OverlayFormatter.Newline) Inlines.Add(new LineBreak());
            else Inlines.Add(new Run(s.Text) { Foreground = HexToBrushConverter.Brush(s.Color) });
        }
    }
}
