namespace Folga.Core.Overlay;

public enum OverlayMetric { Fps, OnePercentLow, FrameTime, Gpu, Vram, GpuTemp, Ping, Cpu, RamFree, Held }

/// <summary>Canto da tela; o número é a posição "sticky" do RTSS (grade 3x3, 0 = superior esquerdo).</summary>
public enum OverlayCorner { TopLeft = 0, TopRight = 2, BottomLeft = 6, BottomRight = 8 }

public enum OverlayLayout { OneLine, Lines }

public sealed class OverlayItem
{
    public OverlayMetric Metric { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Texto antes do número; vazio = só o número.</summary>
    public string Label { get; set; } = "";

    /// <summary>A partir de qual valor o número fica na cor de alerta (para FPS e RAM livre, abaixo dele). null = sem alerta.</summary>
    public double? WarnAt { get; set; }
}

/// <summary>Overlay dentro do jogo, desenhado pelo RTSS com o texto que o Folga escreve na memória dele.</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Só com um jogo aberto. Desligado, aparece em qualquer app 3D que o RTSS desenhe.</summary>
    public bool OnlyDuringGame { get; set; } = true;

    public OverlayLayout Layout { get; set; } = OverlayLayout.Lines;
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;

    /// <summary>Tamanho em relação à fonte do RTSS (100 = a do perfil dele).</summary>
    public int SizePercent { get; set; } = 100;

    /// <summary>Cores em RRGGBB.</summary>
    public string LabelColor { get; set; } = "A0A0A0";
    public string ValueColor { get; set; } = "FFFFFF";
    public string WarnColor { get; set; } = "FF5050";

    public List<OverlayItem> Items { get; set; } = DefaultItems();

    public static List<OverlayItem> DefaultItems() =>
    [
        new() { Metric = OverlayMetric.Fps, Label = "FPS" },
        new() { Metric = OverlayMetric.OnePercentLow, Label = "1% low" },
        new() { Metric = OverlayMetric.FrameTime, Label = "Frametime", Enabled = false },
        new() { Metric = OverlayMetric.Gpu, Label = "GPU" },
        new() { Metric = OverlayMetric.Vram, Label = "VRAM", Enabled = false },
        new() { Metric = OverlayMetric.GpuTemp, Label = "Temp", WarnAt = 83 },
        new() { Metric = OverlayMetric.Ping, Label = "Ping", WarnAt = 80 },
        new() { Metric = OverlayMetric.Cpu, Label = "CPU", Enabled = false },
        new() { Metric = OverlayMetric.RamFree, Label = "RAM livre", WarnAt = 1, Enabled = false },
        new() { Metric = OverlayMetric.Held, Label = "Segurados", Enabled = false },
    ];
}
