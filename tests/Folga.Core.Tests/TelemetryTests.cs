using System.Net;
using Folga.Core.Network;
using Folga.Core.Telemetry;

namespace Folga.Core.Tests;

public sealed class TelemetryTests
{
    private static FrameStats Steady(double frameMs, int frames, double start = 0)
    {
        var stats = new FrameStats();
        for (var i = 0; i <= frames; i++) stats.AddPresent(start + i * frameMs);
        return stats;
    }

    [Fact]
    public void FPS_estavel_de_60()
    {
        var stats = Steady(1000 / 60d, 600);
        Assert.Equal(60, stats.Fps()!.Value, precision: 1);
        Assert.Equal(60, stats.OnePercentLow()!.Value, precision: 1);
    }

    [Fact]
    public void Um_por_cento_low_pega_as_travadinhas_que_a_media_esconde()
    {
        var stats = new FrameStats();
        var t = 0d;
        for (var i = 0; i < 1000; i++)
        {
            stats.AddPresent(t);
            t += i % 50 == 0 ? 50 : 10; // 100 FPS com uma travada de 50 ms a cada 50 quadros
        }
        var average = FrameStats.AverageFps(stats.Recent());
        Assert.InRange(average!.Value, 85, 95);
        Assert.Equal(20, stats.OnePercentLow()!.Value, precision: 1); // 1000 / 50 ms
    }

    [Fact]
    public void Pausa_longa_nao_vira_quadro_e_historico_e_de_60_s()
    {
        var stats = Steady(10, 100);
        stats.AddPresent(100 * 10 + 8_000); // 8 s sem desenhar (alt-tab)
        Assert.DoesNotContain(stats.Recent(), f => f.FrameTimeMs > 1000);

        var longRun = Steady(10, 9_000); // 90 s a 100 FPS
        Assert.InRange(longRun.Recent().Count, 5_990, 6_000);
    }

    [Fact]
    public void Poucos_quadros_nao_dao_1_por_cento_low()
    {
        Assert.Null(Steady(16, 5).OnePercentLow());
        Assert.Null(new FrameStats().Fps());
    }

    [Fact]
    public void Servidor_e_o_destino_publico_com_mais_dados()
    {
        var bytes = new Dictionary<IPAddress, long>
        {
            [IPAddress.Parse("192.168.0.1")] = 900_000,   // roteador
            [IPAddress.Parse("100.70.1.2")] = 800_000,    // CGNAT
            [IPAddress.Parse("35.198.10.20")] = 400_000,  // servidor da partida
            [IPAddress.Parse("52.1.2.3")] = 20_000,       // telemetria
            [IPAddress.Parse("8.8.8.8")] = 100,           // pouco demais
        };
        Assert.Equal(IPAddress.Parse("35.198.10.20"), ServerPicker.Pick(bytes));
        Assert.Null(ServerPicker.Pick(new Dictionary<IPAddress, long> { [IPAddress.Loopback] = 1_000_000 }));
        Assert.False(ServerPicker.IsPublic(IPAddress.Parse("fe80::1")));
        Assert.True(ServerPicker.IsPublic(IPAddress.Parse("2800:3f0:4001::200e")));
    }

    [Fact]
    public void Endereco_do_ETW_em_qualquer_formato()
    {
        var expected = IPAddress.Parse("35.198.10.20");
        var raw = BitConverter.ToInt32(expected.GetAddressBytes()); // in_addr lido como inteiro little-endian
        Assert.Equal(expected, EtwNetworkMonitor.ParseAddress(raw));
        Assert.Equal(expected, EtwNetworkMonitor.ParseAddress((uint)raw));
        Assert.Equal(expected, EtwNetworkMonitor.ParseAddress("35.198.10.20"));
        Assert.Equal(expected, EtwNetworkMonitor.ParseAddress(expected.GetAddressBytes()));
        Assert.Null(EtwNetworkMonitor.ParseAddress(new byte[3]));
    }

    [Fact]
    public void Uso_da_GPU_pelos_contadores_do_Windows_soma_por_placa()
    {
        var engines = new List<(string, double)>
        {
            ("pid_100_luid_0x00000000_0x0000ff81_phys_0_eng_0_engtype_3D", 60),
            ("pid_200_luid_0x00000000_0x0000ff81_phys_0_eng_0_engtype_3D", 25),
            ("pid_100_luid_0x00000000_0x0000ff81_phys_0_eng_3_engtype_Copy", 90),
            ("pid_300_luid_0x00000000_0x00011060_phys_0_eng_0_engtype_3D", 10),
        };
        Assert.Equal(85, GpuMonitor.WindowsUsage(engines));
        Assert.Null(GpuMonitor.WindowsUsage([]));
    }

    [Fact]
    public void GPU_desta_maquina_e_lida()
    {
        using var gpu = new GpuMonitor();
        var snapshot = gpu.Read();
        Assert.NotNull(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Name));
        if (snapshot.Source == "NVML")
        {
            Assert.InRange(snapshot.TemperatureC!.Value, 1, 110);
            Assert.True(snapshot.VramTotalBytes > snapshot.VramUsedBytes);
        }
    }

    [Fact]
    public void Sem_admin_o_FPS_avisa_em_vez_de_quebrar()
    {
        using var telemetry = new TelemetryService(new GpuMonitor());
        telemetry.TrackGame([Environment.ProcessId]);
        var snapshot = telemetry.Snapshot();
        Assert.Null(snapshot.Fps);
        Assert.NotNull(snapshot.FpsNote);
        telemetry.TrackGame([]);
        Assert.Null(telemetry.Snapshot().FpsNote);
    }
}
