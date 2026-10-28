using System.Text.Json;
using Folga.Core.Profiles;
using Folga.Core.Telemetry;

namespace Folga.Core.Tests;

public sealed class CoreUsageTests
{
    [Fact]
    public void Nucleos_escolhidos_viram_os_CPU_Sets_do_jogo()
    {
        var t = TopologyTests.Ryzen5500X3D();
        Assert.Equal([256u, 257u, 258u, 259u, 260u, 261u], t.GameIds(GameCores.Custom, BackgroundCores.Auto, [0, 1, 2]));
        Assert.Equal([266u, 267u], t.GameIds(GameCores.Custom, BackgroundCores.Auto, [5, 99]));   // núcleo inexistente é ignorado
    }

    [Fact]
    public void Escolher_nenhum_ou_todos_e_sem_restricao()
    {
        var t = TopologyTests.Ryzen5500X3D();
        Assert.Empty(t.GameIds(GameCores.Custom, BackgroundCores.Auto, []));
        Assert.Empty(t.GameIds(GameCores.Custom, BackgroundCores.Auto, null));
        Assert.Empty(t.GameIds(GameCores.Custom, BackgroundCores.Auto, [0, 1, 2, 3, 4, 5]));
        Assert.Empty(t.GameIds(GameCores.All, BackgroundCores.Auto, [0])); // a lista só vale com Custom
    }

    [Fact]
    public void Nucleos_escolhidos_ficam_no_config()
    {
        var json = JsonSerializer.Serialize(new GameProfile { Name = "Arc Raiders", Cores = GameCores.Custom, CustomCores = [0, 2] }, ConfigStore.Json);
        Assert.Contains("\"Custom\"", json);
        var back = JsonSerializer.Deserialize<GameProfile>(json, ConfigStore.Json)!;
        Assert.Equal(GameCores.Custom, back.Cores);
        Assert.Equal([0, 2], back.CustomCores);
        Assert.Empty(JsonSerializer.Deserialize<GameProfile>("""{"name":"Velho"}""", ConfigStore.Json)!.CustomCores);
    }

    [Fact]
    public void Amostras_separam_jogo_outros_e_ocioso_por_thread()
    {
        var c = new CoreUsageCounter(processors: 3, gamePids: [100, 101]);
        for (var i = 0; i < 60; i++) c.Add(0, 100);   // thread 0: 60% jogo
        for (var i = 0; i < 30; i++) c.Add(0, 555);   // 30% outro programa
        for (var i = 0; i < 10; i++) c.Add(0, 0);     // 10% ociosa
        for (var i = 0; i < 50; i++) c.Add(1, 101);   // thread 1: o segundo processo do jogo, 100%
        c.Add(7, 100);                                 // processador fora da faixa: ignorado

        var shares = c.Take();
        Assert.Equal(new CoreShare(60, 30), shares[0]);
        Assert.Equal(new CoreShare(100, 0), shares[1]);
        Assert.Null(shares[2]);                        // nenhuma amostra
        Assert.All(c.Take(), s => Assert.Null(s));     // Take zera
    }
}
