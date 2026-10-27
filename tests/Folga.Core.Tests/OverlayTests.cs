using System.IO.MemoryMappedFiles;
using System.Text;
using Folga.Core.Overlay;
using Folga.Core.Profiles;

namespace Folga.Core.Tests;

public sealed class OverlayTests
{
    private static OverlaySettings Settings(params OverlayItem[] items) => new()
    {
        LabelColor = "AAAAAA", ValueColor = "FFFFFF", WarnColor = "FF0000",
        Items = items.Length > 0 ? [.. items] : OverlaySettings.DefaultItems(),
    };

    private static string Plain(IEnumerable<OverlaySegment> segments) => string.Concat(segments.Select(s => s.Text));

    [Fact]
    public void Padrao_mostra_FPS_1low_GPU_temperatura_e_ping_um_por_linha()
    {
        var text = Plain(OverlayFormatter.Build(Settings(), new OverlayValues(Fps: 141.6, OnePercentLow: 97, GpuPercent: 96, GpuTempC: 71, PingMs: 34)));
        Assert.Equal("FPS 142\n1% low 97\nGPU 96%\nTemp 71°C\nPing 34 ms", text);
    }

    [Fact]
    public void Sem_leitura_aparece_traco_e_rotulo_vazio_mostra_so_o_numero()
    {
        var settings = Settings(
            new OverlayItem { Metric = OverlayMetric.Fps, Label = "" },
            new OverlayItem { Metric = OverlayMetric.Ping, Label = "Ping" },
            new OverlayItem { Metric = OverlayMetric.Cpu, Label = "CPU", Enabled = false });
        settings.Layout = OverlayLayout.OneLine;

        Assert.Equal("90   Ping - ms", Plain(OverlayFormatter.Build(settings, new OverlayValues(Fps: 90))));
    }

    [Theory]
    [InlineData(OverlayMetric.Ping, 80d, 81d, true)]
    [InlineData(OverlayMetric.Ping, 80d, 80d, false)]
    [InlineData(OverlayMetric.Fps, 60d, 59d, true)]   // FPS alerta abaixo
    [InlineData(OverlayMetric.Fps, 60d, 144d, false)]
    [InlineData(OverlayMetric.RamFree, 1d, 0.5d, true)]
    public void Alerta_pinta_o_numero(OverlayMetric metric, double limit, double value, bool warn)
    {
        var settings = Settings(new OverlayItem { Metric = metric, Label = "X", WarnAt = limit });
        var values = metric switch
        {
            OverlayMetric.Ping => new OverlayValues(PingMs: value),
            OverlayMetric.Fps => new OverlayValues(Fps: value),
            _ => new OverlayValues(RamFreeGb: value),
        };
        var number = OverlayFormatter.Build(settings, values)[1];
        Assert.Equal(warn ? "FF0000" : "FFFFFF", number.Color);
    }

    [Fact]
    public void Texto_do_RTSS_tem_canto_tamanho_e_cor_e_nao_deixa_o_rotulo_abrir_tag()
    {
        var settings = Settings(new OverlayItem { Metric = OverlayMetric.Fps, Label = "<FR>" }, new OverlayItem { Metric = OverlayMetric.Ping, Label = "" });
        settings.Corner = OverlayCorner.TopRight;
        settings.SizePercent = 150;

        var rtss = OverlayFormatter.ToRtss(settings, OverlayFormatter.Build(settings, new OverlayValues(Fps: 60, PingMs: 20)));

        Assert.Equal("<P2><L0><S=150><C=AAAAAA>(FR) <C><C=FFFFFF>60<C>\n<C=FFFFFF>20<C><C=AAAAAA> ms<C><S>", rtss);
    }

    [Theory]
    [InlineData("#e8a33d", "E8A33D")]
    [InlineData("12345", "FFFFFF")]
    [InlineData("zzzzzz", "FFFFFF")]
    public void Cor_e_normalizada(string input, string expected) => Assert.Equal(expected, OverlayFormatter.NormalizeColor(input));

    [Fact]
    public void Config_antiga_ganha_o_overlay_padrao()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, """{ "version": 3, "games": [] }""");
        var config = new ConfigStore(path).Load();

        Assert.Equal(FolgaConfig.CurrentVersion, config.Version);
        Assert.True(config.Overlay.Enabled);
        Assert.Equal(["FPS", "1% low", "GPU", "Temp", "Ping"], config.Overlay.Items.Where(i => i.Enabled).Select(i => i.Label));
    }
}

/// <summary>Memória compartilhada falsa com o layout do RTSS v2 (cabeçalho + slots de OSD), num nome só do teste.</summary>
public sealed class RtssOsdTests : IDisposable
{
    private const int EntrySize = 512 + 4096 + 64, ArrOffset = 256, Slots = 4;
    private readonly string _name = "FolgaTest-RTSS-" + Guid.NewGuid().ToString("N");
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;

    public RtssOsdTests()
    {
        _map = MemoryMappedFile.CreateNew(_name, ArrOffset + EntrySize * Slots);
        _view = _map.CreateViewAccessor();
        WriteHeader(version: 0x00020015);
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
    }

    private void WriteHeader(uint version, uint signature = 0x52545353)
    {
        _view.Write(0, signature);
        _view.Write(4, version);
        _view.Write(20, (uint)EntrySize);
        _view.Write(24, (uint)ArrOffset);
        _view.Write(28, (uint)Slots);
    }

    private string Field(int slot, int offset, int size)
    {
        var bytes = new byte[size];
        _view.ReadArray(ArrOffset + slot * EntrySize + offset, bytes, 0, size);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.Latin1.GetString(bytes, 0, end < 0 ? size : end);
    }

    private string Owner(int slot) => Field(slot, 256, 256);
    private string Text(int slot) => Field(slot, 512, 4096);
    private uint Frame => _view.ReadUInt32(32);

    private void SetOwner(int slot, string owner)
    {
        var bytes = Encoding.Latin1.GetBytes(owner + "\0");
        _view.WriteArray(ArrOffset + slot * EntrySize + 256, bytes, 0, bytes.Length);
    }

    [Fact]
    public void Pega_o_primeiro_slot_livre_depois_do_0_e_escreve_o_texto()
    {
        SetOwner(1, "MSI Afterburner");
        var osd = new RtssOsd(_name);

        Assert.Equal(RtssStatus.Ok, osd.Update("<C=FFFFFF>FPS 144<C> 71°C"));

        Assert.Equal("", Owner(0)); // o slot 0 fica para o Afterburner/Precision
        Assert.Equal("MSI Afterburner", Owner(1));
        Assert.Equal("Folga", Owner(2));
        Assert.Equal("<C=FFFFFF>FPS 144<C> 71°C", Text(2));
        Assert.Equal(1u, Frame);

        Assert.Equal(RtssStatus.Ok, osd.Update("FPS 60"));
        Assert.Equal("FPS 60", Text(2)); // reaproveita o mesmo slot
        Assert.Equal("", Owner(3));
        Assert.Equal(2u, Frame);
        Assert.Equal(0, _view.ReadInt32(36)); // trava liberada
    }

    [Fact]
    public void Release_limpa_o_slot_e_nao_mexe_nos_outros()
    {
        SetOwner(1, "Outro");
        var osd = new RtssOsd(_name);
        osd.Update("texto");
        osd.Release();

        Assert.Equal("", Owner(2));
        Assert.Equal("", Text(2));
        Assert.Equal("Outro", Owner(1));
    }

    [Fact]
    public void Texto_longo_e_cortado_no_tamanho_do_campo()
    {
        var osd = new RtssOsd(_name);
        osd.Update(new string('x', 5000));
        Assert.Equal(4095, Text(1).Length);
    }

    [Fact]
    public void Slots_cheios_RTSS_fechado_antigo_ou_ocupado()
    {
        for (var i = 1; i < Slots; i++) SetOwner(i, "App" + i);
        Assert.Equal(RtssStatus.NoFreeSlot, new RtssOsd(_name).Update("x"));

        Assert.Equal(RtssStatus.NotRunning, new RtssOsd(_name + "-nao-existe").Update("x"));

        WriteHeader(version: 0x0002000A);
        Assert.Equal(RtssStatus.TooOld, new RtssOsd(_name).Check());

        WriteHeader(version: 0x00020015, signature: 0xDEAD); // RTSS fechando
        Assert.Equal(RtssStatus.NotRunning, new RtssOsd(_name).Check());
    }

    [Fact]
    public void Com_o_RTSS_desenhando_nao_escreve_e_tenta_de_novo_depois()
    {
        SetOwner(1, "Folga");
        _view.Write(36, 1); // bit de ocupado ligado pelo RTSS
        var osd = new RtssOsd(_name);

        Assert.Equal(RtssStatus.Busy, osd.Update("x"));
        Assert.Equal("", Text(1));
        Assert.Equal(1, _view.ReadInt32(36)); // não apaga a trava de quem a tem

        _view.Write(36, 0);
        Assert.Equal(RtssStatus.Ok, osd.Update("x"));
        Assert.Equal("x", Text(1));
    }
}
