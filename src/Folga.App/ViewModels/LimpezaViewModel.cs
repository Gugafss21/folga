using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Folga.Core.Cleanup;
using Folga.Core.Logging;
using Microsoft.Diagnostics.Tracing.Session;

namespace Folga.App.ViewModels;

/// <summary>Uma categoria na tela Limpeza: marcada entra no "Limpar selecionados".</summary>
public sealed partial class CleanupRow(CleanupTarget target, string name, string hint, Action changed) : ObservableObject
{
    public CleanupTarget Target { get; } = target;
    public string Name { get; } = name;
    public string Hint { get; } = hint;
    [ObservableProperty] private bool _selected = target.SelectedByDefault;
    [ObservableProperty] private string _size = "…";
    [ObservableProperty] private long _bytes;
    [ObservableProperty] private string? _blocked;
    public bool CanSelect => Blocked is null;

    partial void OnSelectedChanged(bool value) => changed();
    partial void OnBlockedChanged(string? value) => OnPropertyChanged(nameof(CanSelect));
}

/// <summary>
/// Varre caches e temporários do Windows e dos drivers e apaga os selecionados. Nada de pasta pessoal: a lista
/// é fixa (DiskCleaner), e a tela repete isso para quem for usar.
/// </summary>
public sealed partial class LimpezaViewModel : ObservableObject
{
    private static readonly Dictionary<CleanupKind, (string Name, string Hint)> Texts = new()
    {
        [CleanupKind.UserTemp] = ("Arquivos temporários do seu usuário",
            "Arquivos que os programas deixam na sua pasta Temp e esquecem. Só entram os com mais de 1 dia; os que algum programa está usando ficam."),
        [CleanupKind.WindowsTemp] = ("Arquivos temporários do Windows",
            "A pasta Temp do próprio Windows (C:\\Windows\\Temp), com instaladores e logs que sobraram. Só os com mais de 1 dia; os em uso ficam."),
        [CleanupKind.WindowsUpdate] = ("Downloads do Windows Update",
            "Instaladores de atualizações que o Windows já baixou. Se algum ainda for preciso, o Windows baixa de novo. Fica bloqueado quando uma atualização espera o PC reiniciar."),
        [CleanupKind.DeliveryOptimization] = ("Otimização de Entrega",
            "Pedaços de atualizações e apps da Loja que o Windows guarda para compartilhar com outros PCs. Seguro apagar."),
        [CleanupKind.ErrorReports] = ("Relatórios de erro do Windows",
            "Relatórios de programas que travaram, já enviados à Microsoft ou na fila para enviar."),
        [CleanupKind.CrashDumps] = ("Despejos de memória de travamentos",
            "Arquivos gravados quando o Windows (tela azul) ou um programa trava. Só servem para investigar aquele travamento: se você está tentando descobrir a causa de uma tela azul, não apague ainda."),
        [CleanupKind.ThumbnailCache] = ("Miniaturas do Explorador de Arquivos",
            "Cache das miniaturas de fotos e vídeos. O Windows refaz quando você abre as pastas (fica um pouco mais lento na primeira vez). O que o Explorador estiver usando fica."),
        [CleanupKind.DirectXShaderCache] = ("Cache de shaders do DirectX",
            "Shaders que o Windows já compilou para os jogos. Apagar faz os jogos compilarem tudo de novo: mais travadas nas primeiras partidas. Só vale se estiver muito grande ou se um jogo começou a dar erro gráfico depois de atualizar o driver."),
        [CleanupKind.GpuShaderCache] = ("Cache de shaders da placa de vídeo",
            "Shaders compilados pelo driver da NVIDIA, AMD ou Intel. Pode passar de vários GB, mas é o que evita as travadas de compilação (o driver já tem um limite de tamanho próprio). Apagar = mais travadas nas próximas partidas até o cache se refazer. Desmarcado por isso."),
        [CleanupKind.GpuInstallers] = ("Instaladores antigos do driver de vídeo",
            "Arquivos que o instalador da NVIDIA extrai em C:\\NVIDIA e os downloads do NVIDIA App/GeForce Experience. O driver instalado não depende deles."),
    };

    private readonly Func<bool> _gameOpen;
    private bool _scanned;

    /// <param name="gameOpen">Há um jogo aberto agora (aí nada é apagado: caches em uso).</param>
    public LimpezaViewModel(Func<bool> gameOpen)
    {
        _gameOpen = gameOpen;
        foreach (var target in DiskCleaner.Targets())
        {
            var (name, hint) = Texts[target.Kind];
            Rows.Add(new CleanupRow(target, name, hint, UpdateTotal));
        }
    }

    public ObservableCollection<CleanupRow> Rows { get; } = [];

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _total = "";

    public static bool IsAdmin => TraceEventSession.IsElevated() == true;
    public bool NeedsAdmin => !IsAdmin;

    partial void OnIsVisibleChanged(bool value)
    {
        if (value && !_scanned) ScanCommand.Execute(null);
    }

    private void UpdateTotal()
    {
        var bytes = Rows.Where(r => r.Selected && r.CanSelect).Sum(r => r.Bytes);
        Total = $"Selecionado: {Size(bytes)}";
    }

    [RelayCommand]
    private async Task Scan()
    {
        if (Busy) return;
        Busy = true;
        Status = "Analisando…";
        try
        {
            var now = DateTime.UtcNow;
            var admin = IsAdmin;
            foreach (var row in Rows)
            {
                var scan = await Task.Run(() => DiskCleaner.Scan(row.Target, now, admin));
                row.Bytes = scan.Bytes;
                row.Size = scan.FileCount == 0 ? "vazio" : $"{Size(scan.Bytes)} · {scan.FileCount.ToString("N0", CultureInfo.CurrentCulture)} arquivos";
                row.Blocked = scan.Blocked;
            }
            _scanned = true;
            var available = Rows.Where(r => r.CanSelect).Sum(r => r.Bytes);
            Status = $"Dá para liberar até {Size(available)}.";
            UpdateTotal();
        }
        catch (Exception ex)
        {
            Status = $"A análise falhou: {ex.Message}";
            Log.Error($"Análise de limpeza: {ex}");
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task Clean()
    {
        if (Busy) return;
        if (_gameOpen())
        {
            Status = "Feche o jogo antes de limpar: alguns caches estão em uso por ele.";
            return;
        }
        var chosen = Rows.Where(r => r.Selected && r.CanSelect && r.Bytes > 0).ToList();
        if (chosen.Count == 0)
        {
            Status = "Nada selecionado para limpar.";
            return;
        }
        var answer = MessageBox.Show(
            $"Apagar {Size(chosen.Sum(r => r.Bytes))} destas categorias?\n\n• {string.Join("\n• ", chosen.Select(r => r.Name))}\n\n" +
            "São só caches e temporários do Windows e dos drivers: nenhum arquivo pessoal, nem a Lixeira. Os arquivos apagados não vão para a Lixeira. Arquivos em uso são pulados.",
            "Limpar espaço", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        Busy = true;
        long freed = 0;
        int deleted = 0, skipped = 0;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var row in chosen)
            {
                Status = $"Limpando: {row.Name}…";
                var result = await Task.Run(() => DiskCleaner.Clean(row.Target, now));
                freed += result.FreedBytes;
                deleted += result.Deleted;
                skipped += result.Skipped;
                Log.Info($"Limpeza: {row.Name}: {Size(result.FreedBytes)} em {result.Deleted} arquivos ({result.Skipped} em uso, pulados)");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Limpeza: {ex}");
        }
        finally { Busy = false; }

        await Scan();
        Status = $"Liberados {Size(freed)} ({deleted.ToString("N0", CultureInfo.CurrentCulture)} arquivos)" +
            (skipped > 0 ? $"; {skipped.ToString("N0", CultureInfo.CurrentCulture)} em uso ficaram." : ".");
    }

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
        : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB"
        : bytes > 0 ? "menos de 1 MB" : "0 MB";
}
