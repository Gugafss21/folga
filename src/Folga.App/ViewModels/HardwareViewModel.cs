using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Folga.Core.Diagnostics;
using Folga.Core.Logging;
using Folga.Core.Native;
using Folga.Core.Telemetry;

namespace Folga.App.ViewModels;

/// <summary>
/// Uma linha da ficha: o valor agora e, quando existe, o padrão de fábrica. Changed = você (ou a BIOS) mudou em
/// relação ao padrão; aparece em âmbar.
/// </summary>
public sealed partial class HardwareRow(string name, string hint) : ObservableObject
{
    public string Name { get; } = name;
    public string Hint { get; } = hint;
    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _original = "";
    [ObservableProperty] private bool _changed;

    public HardwareRow Set(string value, string original = "", bool changed = false)
    {
        Value = value;
        Original = original;
        Changed = changed;
        return this;
    }
}

public sealed record HardwareSection(string Title, string Hint, IReadOnlyList<HardwareRow> Rows);

/// <summary>
/// Ficha do PC lida do Windows, sem driver: processador, placa-mãe, memória, placa de vídeo, discos e Windows.
/// Clocks, consumo, temperatura e espaço livre se atualizam a cada 2 s com a tela à vista.
/// </summary>
public sealed partial class HardwareViewModel : ObservableObject
{
    private readonly Func<GpuDetails?> _gpuDetails;
    private readonly DispatcherTimer _timer;
    private readonly PdhCounterArray _cpuPerformance = new(@"\Processor Information(*)\% Processor Performance");
    private CpuDetails? _cpu;
    private int _refreshing;

    // Linhas que mudam com o tempo.
    private readonly HardwareRow _cpuClock = new("Clock agora", "Média das threads, pelo contador de desempenho do Windows. Sobe acima do base com o boost do processador e cai em repouso: não é um ajuste seu.");
    private readonly HardwareRow _gpuCore = new("Clock do núcleo", "Clock do processador gráfico agora e o máximo que o driver permite (boost). Em repouso a placa baixa o clock sozinha. Um máximo acima do de fábrica indica overclock.");
    private readonly HardwareRow _gpuMemory = new("Clock da memória", "Clock da memória de vídeo agora e o máximo. A NVML informa o clock real (metade do \"efetivo\" que outros programas mostram em GDDR5).");
    private readonly HardwareRow _gpuPower = new("Limite de energia", "Quanto a placa pode consumir, agora e o padrão de fábrica. Diferente do padrão: alguém mudou no MSI Afterburner ou no painel da NVIDIA.");
    private readonly HardwareRow _gpuPowerNow = new("Consumo agora", "Consumo da placa de vídeo neste instante, em watts.");
    private readonly HardwareRow _gpuTemp = new("Temperatura", "Temperatura do processador gráfico agora.");
    private readonly HardwareRow _gpuPcie = new("Link PCIe", "Geração e largura do link entre a placa e a placa-mãe, agora e o máximo. Em repouso a geração cai para economizar energia, o que é normal; largura menor que a máxima no jogo (ex.: x8 numa placa x16) indica slot errado ou mal encaixado.");
    private readonly List<(string Drive, HardwareRow Row)> _diskSpace = [];

    /// <param name="gpuDetails">Ficha da placa de vídeo (a telemetria lê a NVML; null fora da NVIDIA).</param>
    public HardwareViewModel(Func<GpuDetails?> gpuDetails)
    {
        _gpuDetails = gpuDetails;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => RefreshLive(), Dispatcher.CurrentDispatcher);
    }

    public ObservableCollection<HardwareSection> Sections { get; } = [];

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _status = "";

    partial void OnIsVisibleChanged(bool value)
    {
        if (!value) { _timer.Stop(); return; }
        if (Sections.Count == 0) LoadAsync();
        else RefreshLive();
        _timer.Start();
    }

    private async void LoadAsync()
    {
        Status = "Lendo o hardware…";
        try
        {
            var (cpu, board, memory, storage, os, gpuRegistry) = await Task.Run(() =>
                (HardwareInfo.ReadCpu(), HardwareInfo.ReadBoard(), HardwareInfo.ReadMemory(), HardwareInfo.ReadStorage(), HardwareInfo.ReadOs(), HardwareInfo.ReadGpuFromRegistry()));
            _cpu = cpu;
            Sections.Clear();
            Sections.Add(CpuSection(cpu));
            Sections.Add(BoardSection(board));
            Sections.Add(MemorySection(memory));
            Sections.Add(GpuSection(gpuRegistry));
            Sections.Add(StorageSection(storage));
            Sections.Add(new HardwareSection("Windows", "Versão do Windows instalada.",
                [new HardwareRow("Versão", "Edição, versão anual e build do Windows.").Set($"{os.Name} {os.Version} (build {os.Build})")]));
            Status = "";
            RefreshLive();
        }
        catch (Exception ex)
        {
            Status = $"Não deu para ler o hardware: {ex.Message}";
            Log.Error($"Leitura do hardware: {ex}");
        }
    }

    private HardwareSection CpuSection(CpuDetails? cpu)
    {
        var rows = new List<HardwareRow>();
        if (cpu is not null)
        {
            rows.Add(new HardwareRow("Modelo", "Nome do processador, como o fabricante grava nele.").Set(cpu.Name));
            rows.Add(new HardwareRow("Núcleos e threads", "Núcleos físicos e threads (com SMT/Hyper-Threading, cada núcleo roda duas).").Set($"{cpu.Cores} núcleos · {cpu.Threads} threads"));
            rows.Add(new HardwareRow("Cache", "Memória rápida dentro do processador, somando todos os núcleos. O L3 grande é o que faz os X3D renderem em jogos.")
                .Set($"L1 {Size(cpu.L1Kb)} · L2 {Size(cpu.L2Kb)} · L3 {Size(cpu.L3Kb)}"));
            rows.Add(new HardwareRow("Clock base", "Clock base que o Windows registra para o processador.").Set(cpu.BaseMhz > 0 ? $"{cpu.BaseMhz:N0} MHz" : "—"));
            rows.Add(_cpuClock);
        }
        return new HardwareSection("Processador", "Lido do registro e da topologia do Windows.", rows);
    }

    private static HardwareSection BoardSection(BoardDetails b)
    {
        var rows = new List<HardwareRow>();
        if (b.BoardModel is not null)
            rows.Add(new HardwareRow("Placa-mãe", "Fabricante e modelo da placa-mãe, gravados no firmware.").Set(Join(b.BoardMaker, b.BoardModel)));
        if (b.SystemModel is not null)
            rows.Add(new HardwareRow("Computador", "Marca e modelo do computador (em PCs montados costuma vir vazio).").Set(Join(b.SystemMaker, b.SystemModel)));
        rows.Add(new HardwareRow("BIOS", "Fabricante, versão e data da BIOS (UEFI). BIOS antiga pode deixar a RAM ou o processador abaixo do que poderiam render.")
            .Set(Join(b.BiosVendor, b.BiosVersion is null ? null : "versão " + b.BiosVersion, b.BiosDate is null ? null : "de " + b.BiosDate)));
        return new HardwareSection("Placa-mãe", "Lido da tabela do firmware que o Windows guarda no registro.", rows);
    }

    private static HardwareSection MemorySection(IReadOnlyList<MemoryModule> modules)
    {
        var rows = new List<HardwareRow>();
        if (modules.Count > 0)
        {
            var total = modules.Sum(m => m.SizeMb);
            rows.Add(new HardwareRow("Total", "Soma dos pentes instalados. Dois pentes iguais em slots da mesma cor rodam em dual channel, o que importa para jogos.")
                .Set($"{Size(total * 1024)} {modules[0].TypeName} em {modules.Count} {(modules.Count == 1 ? "pente" : "pentes")}"));
        }
        foreach (var m in modules)
        {
            rows.Add(new HardwareRow(m.Slot, "Slot, capacidade, fabricante e código do pente, como gravados no próprio pente.")
                .Set(Join(Size(m.SizeMb * 1024), m.TypeName, m.Maker, m.PartNumber, m.Ranks > 0 ? $"{m.Ranks} rank{(m.Ranks > 1 ? "s" : "")}" : null)));
            var speedChanged = m.ConfiguredMhz > 0 && m.RatedMhz > 0 && m.ConfiguredMhz != m.RatedMhz;
            rows.Add(new HardwareRow("   Velocidade", "Velocidade em que a BIOS pôs o pente agora e a padrão informada por ele. Acima da padrão: XMP/DOCP ligado ou ajuste manual. Muitas placas informam a velocidade do XMP como padrão; aí as duas ficam iguais.")
                .Set(m.ConfiguredMhz > 0 ? $"{m.ConfiguredMhz} MT/s" : "—", m.RatedMhz > 0 ? $"{m.RatedMhz} MT/s" : "", speedChanged));
            if (m.ConfiguredMv is { } mv)
                rows.Add(new HardwareRow("   Tensão", "Tensão aplicada no pente agora e a mínima dele (a padrão JEDEC, 1,2 V no DDR4). Acima disso: XMP/DOCP ou ajuste manual.")
                    .Set($"{mv / 1000.0:0.000} V", m.MinMv is { } min ? $"{min / 1000.0:0.000} V" : "", m.MinMv is { } mn && mv > mn));
            var advertised = m.Advertised;
            rows.Add(new HardwareRow("   Latência (CL)", "CL e velocidade que o código do pente anuncia (o perfil XMP de fábrica). O CL que está rodando agora só dá para ler com um driver de kernel, que o Folga não usa por segurança; para ver, abra o ZenTimings ou o CPU-Z com o jogo fechado.")
                .Set(advertised is { } a ? $"CL{a.Cl} a {a.Mhz} MT/s (anunciado)" : "não dá para saber pelo código deste pente"));
        }
        if (modules.Count == 0) rows.Add(new HardwareRow("Memória", "A tabela do firmware não informou os pentes.").Set("sem informação do firmware"));
        return new HardwareSection("Memória", "Lido da tabela SMBIOS do firmware, a mesma que o CPU-Z usa na aba SPD para o que não precisa de driver.", rows);
    }

    private HardwareSection GpuSection((string Name, string? Driver)? registry)
    {
        var details = _gpuDetails();
        var rows = new List<HardwareRow>
        {
            new HardwareRow("Modelo", "Nome da placa de vídeo principal.").Set(details?.Name ?? registry?.Name ?? "—"),
            new HardwareRow("Driver", "Versão do driver de vídeo instalado.").Set(details?.Driver ?? registry?.Driver ?? "—"),
        };
        if (details?.VramTotalBytes is { } vram)
            rows.Add(new HardwareRow("Memória de vídeo", "VRAM da placa. Quando o jogo passa disso, as texturas vão para a RAM e o jogo trava.").Set(Size(vram / 1024)));
        if (details is not null) rows.AddRange([_gpuCore, _gpuMemory, _gpuPower, _gpuPowerNow, _gpuTemp, _gpuPcie]);
        else rows.Add(new HardwareRow("Detalhes", "Clocks, energia e PCIe vêm da NVML, que só existe em placas NVIDIA.").Set("só em placas NVIDIA"));
        return new HardwareSection("Placa de vídeo", "Lido pela NVML, a biblioteca que vem com o driver da NVIDIA.", rows);
    }

    private HardwareSection StorageSection(IReadOnlyList<StorageDetails> drives)
    {
        _diskSpace.Clear();
        var rows = new List<HardwareRow>();
        foreach (var d in drives)
        {
            rows.Add(new HardwareRow($"{d.Drive} {d.Label}".Trim(), "Modelo e tipo do disco onde fica esta letra, e o sistema de arquivos.")
                .Set(Join(d.Model, d.Kind, d.Format)));
            var space = new HardwareRow("   Espaço", "Espaço livre e total. Abaixo de 10% livre, o Windows e os jogos ficam mais lentos para gravar; a tela Limpeza ajuda a liberar.");
            _diskSpace.Add((d.Drive, space));
            rows.Add(SetSpace(space, d.FreeBytes, d.TotalBytes));
        }
        return new HardwareSection("Armazenamento", "Discos fixos, pela consulta de propriedades do Windows.", rows);
    }

    private static HardwareRow SetSpace(HardwareRow row, long free, long total) =>
        row.Set($"{Gb(free)} livres de {Gb(total)}", "", total > 0 && free < total / 10);

    /// <summary>O que muda com o tempo: clock da CPU, a placa de vídeo e o espaço livre.</summary>
    private async void RefreshLive()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var (perf, gpu, drives) = await Task.Run(() => (_cpuPerformance.Read(), _gpuDetails(), DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => (Drive: d.Name[..2], d.AvailableFreeSpace, d.TotalSize)).ToList()));

            if (_cpu is { BaseMhz: > 0 } cpu && perf?.FirstOrDefault(p => p.Instance == "_Total") is { Instance: not null } total)
                _cpuClock.Set($"{cpu.BaseMhz * total.Value / 100:N0} MHz", $"base {cpu.BaseMhz:N0} MHz");

            if (gpu is not null)
            {
                _gpuCore.Set(Mhz(gpu.CoreMhz), gpu.CoreMaxMhz is { } cm ? $"máx. {cm:N0} MHz" : "");
                _gpuMemory.Set(Mhz(gpu.MemoryMhz), gpu.MemoryMaxMhz is { } mm ? $"máx. {mm:N0} MHz" : "");
                _gpuPower.Set(gpu.PowerLimitW is { } pl ? $"{pl} W" : "—", gpu.PowerDefaultW is { } pd ? $"{pd} W" : "",
                    gpu.PowerLimitW is { } l && gpu.PowerDefaultW is { } d && l != d);
                _gpuPowerNow.Set(gpu.PowerNowW is { } pn ? $"{pn} W" : "—");
                _gpuTemp.Set(gpu.TemperatureC is { } t ? $"{t} °C" : "—");
                _gpuPcie.Set(Link(gpu.PcieGen, gpu.PcieWidth), Link(gpu.PcieMaxGen, gpu.PcieMaxWidth), gpu.PcieWidth < gpu.PcieMaxWidth);
            }

            foreach (var (drive, row) in _diskSpace)
                if (drives.FirstOrDefault(d => d.Drive == drive) is { TotalSize: > 0 } d) SetSpace(row, d.AvailableFreeSpace, d.TotalSize);
        }
        catch (Exception ex)
        {
            Log.Warn($"Atualização do hardware: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private static string Mhz(int? mhz) => mhz is { } m ? $"{m:N0} MHz" : "—";
    private static string Link(int? gen, int? width) => gen is null && width is null ? "" : $"Gen {gen?.ToString() ?? "?"} x{width?.ToString() ?? "?"}";

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } s ? s : "—";

    /// <summary>Tamanho a partir de KB, na maior unidade que der número redondo.</summary>
    private static string Size(long kb) =>
        kb >= 1024 * 1024 ? (kb / 1024.0 / 1024).ToString("0.#", CultureInfo.CurrentCulture) + " GB"
        : kb >= 1024 ? (kb / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB"
        : $"{kb} KB";

    private static string Gb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("0", CultureInfo.CurrentCulture) + " GB";
}
