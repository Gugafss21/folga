using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Folga.Core.Cleanup;
using Folga.Core.Diagnostics;

namespace Folga.Core.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _outside = new();
    public void Dispose()
    {
        _dir.Dispose();
        _outside.Dispose();
    }

    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private string Make(string relative, int bytes, TimeSpan age, string? root = null)
    {
        var path = Path.Combine(root ?? _dir.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    [Fact]
    public void Apaga_so_o_que_e_velho_e_deixa_a_pasta()
    {
        var old = Make(@"a\velho.tmp", 1000, TimeSpan.FromDays(3));
        var fresh = Make(@"a\novo.tmp", 500, TimeSpan.FromHours(2));
        var target = new CleanupTarget(CleanupKind.UserTemp, [_dir.Path], TimeSpan.FromDays(1));

        var scan = DiskCleaner.Scan(target, Now, isAdmin: false);
        Assert.Equal(1000, scan.Bytes);
        Assert.Equal(1, scan.FileCount);
        Assert.Null(scan.Blocked);

        var result = DiskCleaner.Clean(target, Now, TestContext.Current.CancellationToken);
        Assert.Equal(1000, result.FreedBytes);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(Directory.Exists(_dir.Path));
    }

    [Fact]
    public void Arquivo_em_uso_fica()
    {
        var locked = Make("preso.log", 100, TimeSpan.FromDays(5));
        Make("solto.log", 100, TimeSpan.FromDays(5));
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = DiskCleaner.Clean(new CleanupTarget(CleanupKind.UserTemp, [_dir.Path], TimeSpan.Zero), Now, TestContext.Current.CancellationToken);
            Assert.Equal(1, result.Deleted);
            Assert.Equal(1, result.Skipped);
        }
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void Nao_segue_juncao_para_fora_da_pasta()
    {
        var personal = Make("pessoal.docx", 100, TimeSpan.FromDays(30), _outside.Path);
        var link = Path.Combine(_dir.Path, "atalho");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_outside.Path}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(link));
        try
        {
            var target = new CleanupTarget(CleanupKind.UserTemp, [_dir.Path], TimeSpan.Zero);
            Assert.Equal(0, DiskCleaner.Scan(target, Now, false).FileCount);
            DiskCleaner.Clean(target, Now, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(personal));
            Assert.True(Directory.Exists(link)); // nem a junção é tirada
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void Padrao_e_nivel_unico()
    {
        var thumb = Make("thumbcache_256.db", 10, TimeSpan.FromDays(2));
        var other = Make("iconcache_32.db", 10, TimeSpan.FromDays(2));
        var nested = Make(@"sub\thumbcache_96.db", 10, TimeSpan.FromDays(2));
        DiskCleaner.Clean(new CleanupTarget(CleanupKind.ThumbnailCache, [_dir.Path], TimeSpan.Zero, Recursive: false, Pattern: "thumbcache_*.db"), Now, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(thumb));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Subpasta_vazia_velha_sai_e_a_nova_fica()
    {
        Make(@"velha\x.tmp", 10, TimeSpan.FromDays(3));
        var oldDir = Path.Combine(_dir.Path, "velha");
        Directory.SetCreationTimeUtc(oldDir, Now - TimeSpan.FromDays(3));
        var newDir = Path.Combine(_dir.Path, "nova");
        Directory.CreateDirectory(newDir);
        Directory.SetCreationTimeUtc(newDir, Now);

        DiskCleaner.Clean(new CleanupTarget(CleanupKind.UserTemp, [_dir.Path], TimeSpan.FromDays(1)), Now, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(oldDir));
        Assert.True(Directory.Exists(newDir));
    }

    [Fact]
    public void Pastas_pessoais_e_raizes_sao_recusadas()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(DiskCleaner.IsSafeRoot(@"C:\"));
        Assert.False(DiskCleaner.IsSafeRoot(@"C:\Windows"));
        Assert.False(DiskCleaner.IsSafeRoot(profile));
        Assert.False(DiskCleaner.IsSafeRoot(Path.Combine(profile, "Downloads")));
        Assert.False(DiskCleaner.IsSafeRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Jogos", "Saves")));
        Assert.False(DiskCleaner.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        Assert.True(DiskCleaner.IsSafeRoot(_dir.Path));

        var docs = Make("importante.txt", 10, TimeSpan.FromDays(9), Path.Combine(profile, "Downloads", "nao-existe-" + Guid.NewGuid().ToString("N")[..6]));
        try
        {
            var r = DiskCleaner.Clean(new CleanupTarget(CleanupKind.UserTemp, [Path.Combine(profile, "Downloads")], TimeSpan.Zero), Now, TestContext.Current.CancellationToken);
            Assert.Equal(0, r.Deleted);
            Assert.True(File.Exists(docs));
        }
        finally { Directory.Delete(Path.GetDirectoryName(docs)!, true); }
    }

    [Fact]
    public void Categorias_do_PC_sao_pastas_seguras()
    {
        foreach (var t in DiskCleaner.Targets())
        {
            Assert.All(t.Folders, f => Assert.True(DiskCleaner.IsSafeRoot(f), $"{t.Kind}: {f}"));
            Assert.Equal(t.Kind is not (CleanupKind.DirectXShaderCache or CleanupKind.GpuShaderCache), t.SelectedByDefault);
        }
        Assert.Contains(DiskCleaner.Targets(), t => t.Kind == CleanupKind.WindowsTemp && DiskCleaner.Blocked(t, isAdmin: false) is not null);
    }

    [Theory]
    [InlineData("KF432C16BB/8", 3200, 16)]
    [InlineData("F4-3600C18-8GVK", 3600, 18)]
    [InlineData("CMK16GX4M2B3200C16", 3200, 16)]
    [InlineData("BL8G32C16U4B.M8FE1", 3200, 16)]
    [InlineData("TF3D416G3200HC16FDC01", 3200, 16)]
    [InlineData("AX4U32008G16A-SB10", 3200, 16)]
    public void CL_anunciado_pelo_codigo_do_pente(string part, int mhz, int cl) =>
        Assert.Equal((mhz, cl), PartNumbers.Decode(part));

    [Theory]
    [InlineData("HKED4081CAA2F2HB2")]
    [InlineData("CT8G4DFS832A.C8FE")]
    [InlineData("")]
    [InlineData(null)]
    public void Codigo_desconhecido_fica_sem_CL(string? part) => Assert.Null(PartNumbers.Decode(part));

    [Fact]
    public void Pente_lido_da_tabela_SMBIOS()
    {
        // Estrutura 17 com 0x28 bytes + strings: slot, banco, fabricante, código.
        var s = new byte[0x28];
        s[0] = 17; s[1] = 0x28;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x0C), 8192);  // MB
        s[0x10] = 1; s[0x11] = 2; s[0x12] = 26;                          // DDR4
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x15), 2666);
        s[0x17] = 3; s[0x1A] = 4; s[0x1B] = 2;                           // 2 ranks
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x20), 3200);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x22), 1200);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x26), 1350);
        var strings = Encoding.ASCII.GetBytes("DIMM_A2\0BANK 1\0Kingston\0KF432C16BB/8\0\0");
        var end = new byte[] { 127, 4, 0, 0, 0, 0 };
        var table = s.Concat(strings).Concat(end).ToArray();

        var m = Assert.Single(HardwareInfo.ParseMemory(table));
        Assert.Equal("BANK 1 DIMM_A2", m.Slot);
        Assert.Equal("Kingston", m.Maker);
        Assert.Equal("DDR4", m.TypeName);
        Assert.Equal((2666, 3200), (m.RatedMhz, m.ConfiguredMhz));
        Assert.Equal((1200, 1350), (m.MinMv, m.ConfiguredMv));
        Assert.Equal(2, m.Ranks);
        Assert.Equal((3200, 16), m.Advertised);
    }

    [Fact]
    public void Leitura_real_do_hardware()
    {
        var cpu = HardwareInfo.ReadCpu();
        Assert.NotNull(cpu);
        Assert.True(cpu.Threads >= cpu.Cores && cpu.Cores > 0);
        Assert.NotEmpty(HardwareInfo.ReadStorage());
        Assert.StartsWith("Windows", HardwareInfo.ReadOs().Name);
    }
}
