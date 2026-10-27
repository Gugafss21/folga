using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Folga.App.Services;
using Folga.Core.Overlay;

namespace Folga.App.ViewModels;

public sealed partial class OverlayItemRow : ObservableObject
{
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _warnText = "";

    public OverlayMetric Metric { get; init; }
    public string Name => Names[Metric];
    public string WarnUnit => (OverlayFormatter.LowIsBad(Metric) ? "abaixo de " : "acima de ") + Units[Metric];

    private static readonly Dictionary<OverlayMetric, string> Names = new()
    {
        [OverlayMetric.Fps] = "FPS", [OverlayMetric.OnePercentLow] = "1% low", [OverlayMetric.FrameTime] = "Frametime",
        [OverlayMetric.Gpu] = "Uso da GPU", [OverlayMetric.Vram] = "VRAM em uso", [OverlayMetric.GpuTemp] = "Temperatura da GPU",
        [OverlayMetric.Ping] = "Ping", [OverlayMetric.Cpu] = "Uso da CPU", [OverlayMetric.RamFree] = "RAM livre",
        [OverlayMetric.Held] = "Apps segurados",
    };

    private static readonly Dictionary<OverlayMetric, string> Units = new()
    {
        [OverlayMetric.Fps] = "FPS", [OverlayMetric.OnePercentLow] = "FPS", [OverlayMetric.FrameTime] = "ms",
        [OverlayMetric.Gpu] = "%", [OverlayMetric.Vram] = "GB", [OverlayMetric.GpuTemp] = "°C",
        [OverlayMetric.Ping] = "ms", [OverlayMetric.Cpu] = "%", [OverlayMetric.RamFree] = "GB", [OverlayMetric.Held] = "apps",
    };

    public static OverlayItemRow From(OverlayItem i) => new()
    {
        Metric = i.Metric, Enabled = i.Enabled, Label = i.Label,
        WarnText = i.WarnAt is { } w ? w.ToString("0.#", CultureInfo.CurrentCulture) : "",
    };

    public OverlayItem ToModel() => new()
    {
        Metric = Metric, Enabled = Enabled, Label = Label.Trim(),
        WarnAt = double.TryParse(WarnText, NumberStyles.Float, CultureInfo.CurrentCulture, out var w) ? w : null,
    };
}

public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _loading;

    public OverlayViewModel(AppServices services)
    {
        _services = services;
        Rows.CollectionChanged += (_, e) =>
        {
            foreach (OverlayItemRow row in e.NewItems ?? Array.Empty<OverlayItemRow>()) row.PropertyChanged += OnEdited;
            RebuildPreview();
        };
        PropertyChanged += OnEdited;
        Reload();
        ShowRtss(services.Overlay.State);
        services.Overlay.StateChanged += state => Application.Current?.Dispatcher.BeginInvoke(() => ShowRtss(state));
    }

    public ObservableCollection<OverlayItemRow> Rows { get; } = [];

    [ObservableProperty] private OverlayItemRow? _selectedRow;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _onlyDuringGame;
    [ObservableProperty] private OverlayLayout _layout;
    [ObservableProperty] private OverlayCorner _corner;
    [ObservableProperty] private string _sizeText = "100";
    [ObservableProperty] private string _labelColor = "";
    [ObservableProperty] private string _valueColor = "";
    [ObservableProperty] private string _warnColor = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _rtssText = "";
    [ObservableProperty] private bool _canOpenRtss;
    [ObservableProperty] private IReadOnlyList<OverlaySegment> _preview = [];
    [ObservableProperty] private double _previewFontSize = 14;
    [ObservableProperty] private HorizontalAlignment _previewHorizontal;
    [ObservableProperty] private VerticalAlignment _previewVertical;

    private void Reload()
    {
        _loading = true;
        var s = _services.Engine.Config.Overlay;
        Enabled = s.Enabled;
        OnlyDuringGame = s.OnlyDuringGame;
        Layout = s.Layout;
        Corner = s.Corner;
        SizeText = s.SizePercent.ToString(CultureInfo.CurrentCulture);
        LabelColor = s.LabelColor;
        ValueColor = s.ValueColor;
        WarnColor = s.WarnColor;
        Rows.Clear();
        foreach (var item in s.Items) Rows.Add(OverlayItemRow.From(item));
        _loading = false;
        RebuildPreview();
    }

    private OverlaySettings ToModel() => new()
    {
        Enabled = Enabled, OnlyDuringGame = OnlyDuringGame, Layout = Layout, Corner = Corner,
        SizePercent = int.TryParse(SizeText, out var size) ? Math.Clamp(size, 50, 200) : 100,
        LabelColor = OverlayFormatter.NormalizeColor(LabelColor),
        ValueColor = OverlayFormatter.NormalizeColor(ValueColor),
        WarnColor = OverlayFormatter.NormalizeColor(WarnColor),
        Items = Rows.Select(r => r.ToModel()).ToList(),
    };

    private void OnEdited(object? sender, PropertyChangedEventArgs e)
    {
        // Só o que muda o desenho; as propriedades da própria prévia e do status não.
        if (_loading || e.PropertyName is nameof(Preview) or nameof(PreviewFontSize) or nameof(PreviewHorizontal) or nameof(PreviewVertical)
            or nameof(Status) or nameof(RtssText) or nameof(CanOpenRtss) or nameof(SelectedRow)) return;
        RebuildPreview();
    }

    /// <summary>A prévia usa números de exemplo; no jogo, entram os de verdade.</summary>
    private void RebuildPreview()
    {
        if (_loading) return;
        var settings = ToModel();
        Preview = OverlayFormatter.Build(settings, OverlayFormatter.Sample);
        PreviewFontSize = 14 * settings.SizePercent / 100d;
        PreviewHorizontal = settings.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        PreviewVertical = settings.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight ? VerticalAlignment.Bottom : VerticalAlignment.Top;
    }

    private void ShowRtss(OverlayState state)
    {
        RtssText = state.Rtss switch
        {
            RtssStatus.Ok when state.Showing => "RTSS aberto · overlay no ar.",
            RtssStatus.Ok => Enabled
                ? OnlyDuringGame ? "RTSS aberto · o overlay aparece quando um jogo abrir." : "RTSS aberto · o overlay aparece em apps 3D."
                : "RTSS aberto · overlay desligado.",
            RtssStatus.NotRunning => "RTSS fechado: o overlay só aparece com o RTSS aberto.",
            _ => "RTSS " + OverlayService.Describe(state.Rtss) + ".",
        };
        CanOpenRtss = state.Rtss == RtssStatus.NotRunning && RtssOsd.InstallPath() is not null;
    }

    [RelayCommand]
    private void OpenRtss()
    {
        if (RtssOsd.InstallPath() is not { } path) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Status = "Abrindo o RTSS...";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Status = $"Não deu para abrir o RTSS: {ex.Message}";
        }
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedRow is not { } row) return;
        var from = Rows.IndexOf(row);
        var to = from + delta;
        if (to < 0 || to >= Rows.Count) return;
        Rows.Move(from, to);
        SelectedRow = row;
        RebuildPreview();
    }

    [RelayCommand]
    private void ResetItems()
    {
        Rows.Clear();
        foreach (var item in OverlaySettings.DefaultItems()) Rows.Add(OverlayItemRow.From(item));
        Status = "Itens voltaram ao padrão. Salve para aplicar.";
    }

    [RelayCommand]
    private void Save()
    {
        _services.Engine.UpdateOverlay(ToModel());
        _services.SaveEngineConfig();
        Reload();
        ShowRtss(_services.Overlay.State);
        Status = "Salvo.";
    }
}
