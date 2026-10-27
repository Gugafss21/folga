using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Win32;

namespace Folga.Core.Overlay;

public enum RtssStatus { NotRunning, Ok, Busy, NoFreeSlot, TooOld }

/// <summary>
/// Escreve o texto do overlay num slot próprio da memória compartilhada do RTSS ("RTSSSharedMemoryV2"),
/// como o exemplo RTSSSharedMemorySample do SDK. O RTSS desenha o texto em todo app 3D que ele acompanha.
/// </summary>
public sealed unsafe class RtssOsd(string mapName = RtssOsd.DefaultMapName)
{
    public const string DefaultMapName = "RTSSSharedMemoryV2";
    internal const string Owner = "Folga";
    private const uint Signature = 0x52545353; // 'RTSS'
    private const uint VersionTags = 0x0002000B, VersionBusy = 0x0002000E; // v2.11: marcações (e szOSDEx, desde v2.7); v2.14: trava

    // RTSS_SHARED_MEMORY: deslocamentos dos campos do cabeçalho.
    private const int OffVersion = 4, OffOsdEntrySize = 20, OffOsdArrOffset = 24, OffOsdArrSize = 28, OffOsdFrame = 32, OffBusy = 36;
    // RTSS_SHARED_MEMORY_OSD_ENTRY: szOSD[256], szOSDOwner[256], szOSDEx[4096].
    private const int EntryOwner = 256, EntryOsdEx = 512, OwnerSize = 256, OsdExSize = 4096;

    /// <summary>Texto no formato do RTSS: ANSI. Latin-1 cobre os acentos do português e o °.</summary>
    private static readonly Encoding Ansi = Encoding.Latin1;

    /// <summary>Grava o texto no slot do Folga (pega um livre na primeira vez).</summary>
    public RtssStatus Update(string text) => WithMemory(p =>
    {
        var entry = FindEntry(p, claim: true);
        if (entry is null) return RtssStatus.NoFreeSlot;

        var useLock = Read(p, OffVersion) >= VersionBusy;
        if (useLock && (Interlocked.Or(ref *(int*)(p + OffBusy), 1) & 1) != 0) return RtssStatus.Busy; // o RTSS está desenhando
        try
        {
            WriteString(entry + EntryOsdEx, OsdExSize, text);
        }
        finally
        {
            if (useLock) Interlocked.And(ref *(int*)(p + OffBusy), ~1);
        }
        Interlocked.Increment(ref *(int*)(p + OffOsdFrame));
        return RtssStatus.Ok;
    });

    /// <summary>Só verifica se o RTSS está aberto e é compatível, sem escrever nada.</summary>
    public RtssStatus Check() => WithMemory(_ => RtssStatus.Ok);

    /// <summary>Libera o slot (o overlay some). Sem o RTSS aberto, não há nada a fazer.</summary>
    public void Release() => WithMemory(p =>
    {
        var entry = FindEntry(p, claim: false);
        if (entry != null)
        {
            new Span<byte>(entry, (int)Read(p, OffOsdEntrySize)).Clear();
            Interlocked.Increment(ref *(int*)(p + OffOsdFrame));
        }
        return RtssStatus.Ok;
    });

    /// <summary>Onde o RTSS está instalado (para o botão de abrir); null se não estiver.</summary>
    public static string? InstallPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Unwinder\RTSS");
        return key?.GetValue("InstallPath") is string path && File.Exists(path) ? path : null;
    }

    private RtssStatus WithMemory(Func<IntPtr, RtssStatus> action)
    {
        MemoryMappedFile map;
        try { map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite); }
        catch (FileNotFoundException) { return RtssStatus.NotRunning; }

        using (map)
        using (var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            try
            {
                if (Read((IntPtr)p, 0) != Signature) return RtssStatus.NotRunning; // 0xDEAD: o RTSS está fechando
                if (Read((IntPtr)p, OffVersion) < VersionTags) return RtssStatus.TooOld;
                return action((IntPtr)p);
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    /// <summary>
    /// Procura o slot do Folga; com <paramref name="claim"/>, pega o primeiro livre se ainda não tem.
    /// O slot 0 fica com o Afterburner/Precision, como o SDK recomenda.
    /// </summary>
    private static byte* FindEntry(IntPtr mem, bool claim)
    {
        var p = (byte*)mem;
        var size = Read(mem, OffOsdEntrySize);
        var offset = Read(mem, OffOsdArrOffset);
        var count = Read(mem, OffOsdArrSize);
        for (var pass = 0; pass < (claim ? 2 : 1); pass++)
        {
            for (uint i = 1; i < count; i++)
            {
                var entry = p + offset + i * size;
                var owner = ReadString(entry + EntryOwner, OwnerSize);
                if (pass == 1 && owner.Length == 0)
                {
                    WriteString(entry + EntryOwner, OwnerSize, Owner);
                    owner = Owner;
                }
                if (owner == Owner) return entry;
            }
        }
        return null;
    }

    private static uint Read(IntPtr mem, int offset) => *(uint*)((byte*)mem + offset);

    private static string ReadString(byte* at, int size)
    {
        var span = new ReadOnlySpan<byte>(at, size);
        var end = span.IndexOf((byte)0);
        return Ansi.GetString(end < 0 ? span : span[..end]);
    }

    /// <summary>Copia como ANSI terminado em zero, cortando no tamanho do campo.</summary>
    private static void WriteString(byte* at, int size, string text)
    {
        var bytes = Ansi.GetBytes(text);
        var length = Math.Min(bytes.Length, size - 1);
        var target = new Span<byte>(at, size);
        bytes.AsSpan(0, length).CopyTo(target);
        target[length] = 0;
    }
}
