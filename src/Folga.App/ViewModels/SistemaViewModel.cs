using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Folga.App.Services;
using Folga.Core.Logging;
using Folga.Core.Profiles;
using Folga.Core.SystemSettings;
using Compression = Folga.Core.SystemSettings.MemoryCompression;
using Shortcut = Folga.Core.SystemSettings.StartMenuShortcut;

namespace Folga.App.ViewModels;

/// <summary>
/// Ajustes do próprio Windows: paginação, memória compactada, Modo de Jogo, otimizações de janela,
/// capturas do Game Bar, agendamento de GPU e plano de energia durante o jogo. Lê o estado real do
/// Windows ao abrir e só grava o que mudou ao clicar em Aplicar.
/// </summary>
public sealed partial class SistemaViewModel : ObservableObject
{
    private const string KeyPagefile = "pagefile", KeyCompression = "compression", KeyHags = "hags";

    private readonly AppServices _services;
    private PagefileSetting _loadedPagefile = new(PagefileMode.SystemManaged);
    private bool? _loadedCompression;
    private GamingSnapshot? _loadedGaming;
    private string? _loadedStartupPath, _loadedShortcutTarget;
    private readonly string _exePath = Environment.ProcessPath ?? "Folga.exe";

    public SistemaViewModel(AppServices services)
    {
        _services = services;
        Drives = [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name[..2].ToUpperInvariant())];
        IsAdmin = Environment.IsPrivilegedProcess;
        _ = LoadAsync();
    }

    public bool IsAdmin { get; }
    public bool NeedsAdmin => !IsAdmin;
    public IReadOnlyList<string> Drives { get; }
    public ObservableCollection<Choice<Guid?>> PowerSchemes { get; } = [];

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _restartText = "";
    [ObservableProperty] private bool _restartPending;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _startupNow = "";
    [ObservableProperty] private bool _startMenuShortcut;
    [ObservableProperty] private string _shortcutNow = "";

    [ObservableProperty] private PagefileMode _pagefileMode;
    [ObservableProperty] private string _pagefileDrive = "C:";
    [ObservableProperty] private string _initialMbText = "";
    [ObservableProperty] private string _maximumMbText = "";
    [ObservableProperty] private string _pagefileNow = "";
    [ObservableProperty] private string _pagefileAdvice = "";
    [ObservableProperty] private string? _pagefileWarning;

    [ObservableProperty] private bool _memoryCompression;
    [ObservableProperty] private bool _compressionKnown;
    [ObservableProperty] private string _compressionNow = "";
    [ObservableProperty] private string _compressionAdvice = "";

    [ObservableProperty] private bool _gameMode;
    [ObservableProperty] private bool _windowedOptimizations;
    [ObservableProperty] private bool _gameBarCapture;
    [ObservableProperty] private bool _hardwareGpuScheduling;
    [ObservableProperty] private string _hagsNow = "";

    [ObservableProperty] private Guid? _powerScheme;
    [ObservableProperty] private string _powerNow = "";

    public bool IsCustomPagefile => PagefileMode == PagefileMode.Custom;

    partial void OnPagefileModeChanged(PagefileMode value)
    {
        OnPropertyChanged(nameof(IsCustomPagefile));
        UpdatePagefileWarning();
    }

    partial void OnInitialMbTextChanged(string value) => UpdatePagefileWarning();
    partial void OnMaximumMbTextChanged(string value) => UpdatePagefileWarning();

    private PagefileSetting DesiredPagefile() => new(PagefileMode, PagefileDrive,
        int.TryParse(InitialMbText, out var i) ? i : 0, int.TryParse(MaximumMbText, out var m) ? m : 0);

    private void UpdatePagefileWarning() => PagefileWarning = Pagefile.Warning(DesiredPagefile(), Pagefile.Commit());

    /// <summary>Lê tudo do Windows. A memória compactada vem do PowerShell (1–2 s), então fora da interface.</summary>
    private async Task LoadAsync()
    {
        IsLoading = true;
        var (pagefile, compression, compressionRunning, gaming, schemes, active) = await Task.Run(() =>
            (Pagefile.Read(), Compression.ReadConfigured(), Compression.IsRunning(), GamingSettings.Read(),
             _services.Power.List(), _services.Power.Active()));
        (_loadedStartupPath, _loadedShortcutTarget) = await Task.Run(() => (StartupTask.RegisteredPath(), Shortcut.Target()));

        StartWithWindows = _loadedStartupPath is not null;
        StartupNow = _loadedStartupPath is null ? "Desligado: o Folga só abre quando você abrir."
            : StartupTask.SamePath(_loadedStartupPath, _exePath) ? "Ligado: abre minimizado na bandeja 10 s depois de entrar no Windows, já como administrador."
            : $"A tarefa abre outro Folga.exe ({_loadedStartupPath}). Clique em Aplicar para ela abrir este.";
        StartMenuShortcut = _loadedShortcutTarget is not null;
        ShortcutNow = _loadedShortcutTarget is null ? "Sem atalho no Menu Iniciar."
            : StartupTask.SamePath(_loadedShortcutTarget, _exePath) ? "O atalho \"Folga\" está no Menu Iniciar."
            : $"O atalho aponta para outro Folga.exe ({_loadedShortcutTarget}). Clique em Aplicar para apontar para este.";

        var commit = Pagefile.Commit();
        var ramGb = commit.RamBytes / (1024d * 1024 * 1024);
        var ramMb = commit.RamBytes >> 20;

        _loadedPagefile = pagefile;
        PagefileMode = pagefile.Mode;
        PagefileDrive = pagefile.Drive == "?:" ? Drives.FirstOrDefault() ?? "C:" : pagefile.Drive;
        // A RAM utilizável fica um pouco abaixo da instalada (15,9 de 16 GB): a sugestão usa o tamanho instalado.
        var suggestedMax = (long)Math.Ceiling(ramMb / 1024d) * 1024;
        var suggestedMin = Math.Max(4096, suggestedMax / 2);
        InitialMbText = (pagefile.Mode == PagefileMode.Custom ? pagefile.InitialMb : suggestedMin).ToString(CultureInfo.InvariantCulture);
        MaximumMbText = (pagefile.Mode == PagefileMode.Custom ? pagefile.MaximumMb : suggestedMax).ToString(CultureInfo.InvariantCulture);
        PagefileNow = $"Agora: {Gb(commit.PagefileBytes)} GB de paginação. Limite de memória (RAM + paginação) {Gb(commit.CommitLimitBytes)} GB, {Gb(commit.CommitUsedBytes)} GB reservados.";
        PagefileAdvice = $"Com {ramGb:0} GB de RAM: deixe gerenciada pelo Windows. Se preferir tamanho fixo, use {suggestedMin} MB de mínimo e {suggestedMax} MB de máximo, num SSD.";

        _loadedCompression = compression;
        CompressionKnown = compression is not null;
        MemoryCompression = compression ?? compressionRunning;
        CompressionNow = (compressionRunning ? "Ativa neste boot." : "Desligada neste boot.")
            + (compression is null ? " Não deu para ler a configuração (precisa do Folga como administrador)." : "");
        CompressionAdvice = ramGb <= 16.5
            ? $"Com {ramGb:0} GB de RAM: deixe ligada. Comprimir na RAM é muito mais rápido que mandar para o disco, e evita travadas quando a memória aperta."
            : $"Com {ramGb:0} GB de RAM a compactação quase não é usada; desligar economiza um pouco de CPU, mas o ganho em jogo é pequeno.";

        _loadedGaming = gaming;
        GameMode = gaming.GameMode;
        WindowedOptimizations = gaming.WindowedOptimizations;
        GameBarCapture = gaming.GameBarCapture;
        HardwareGpuScheduling = gaming.HardwareGpuScheduling ?? false;
        HagsNow = gaming.HardwareGpuScheduling is null ? "Não configurado: vale o padrão do driver." : "";

        var activeName = schemes.FirstOrDefault(s => s.Id == active)?.Name ?? "desconhecido";
        PowerSchemes.Clear();
        PowerSchemes.Add(new Choice<Guid?>(null, $"Não trocar (fica o atual: {activeName})", "Durante o jogo o plano de energia continua o que está ativo no Windows."));
        foreach (var s in schemes)
            PowerSchemes.Add(new Choice<Guid?>(s.Id, s.Name, $"Durante o jogo o Windows usa o plano \"{s.Name}\"; ao fechar o jogo, volta o plano que estava antes."));
        PowerScheme = _services.Engine.Config.Power.SchemeDuringGame;
        PowerNow = $"Plano ativo agora: {activeName}.";

        UpdatePagefileWarning();
        ShowRestart();
        IsLoading = false;
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (IsLoading || _loadedGaming is not { } before) return;
        var desired = DesiredPagefile();
        var messages = new List<string>();

        await Task.Run(() =>
        {
            if (StartWithWindows && !StartupTask.SamePath(_loadedStartupPath, _exePath))
                Try(messages, "Iniciar com o Windows", () =>
                {
                    if (StartupTask.Register(_exePath) is { } error) throw new InvalidOperationException(error);
                    Log.Info($"Folga vai abrir com o Windows ({_exePath})");
                });
            else if (!StartWithWindows && _loadedStartupPath is not null)
                Try(messages, "Iniciar com o Windows", () =>
                {
                    if (StartupTask.Remove() is { } error) throw new InvalidOperationException(error);
                    Log.Info("Folga não abre mais com o Windows");
                });

            if (StartMenuShortcut && !StartupTask.SamePath(_loadedShortcutTarget, _exePath))
                Try(messages, "Atalho", () => Shortcut.Create(_exePath));
            else if (!StartMenuShortcut && _loadedShortcutTarget is not null)
                Try(messages, "Atalho", Shortcut.Remove);

            if (!Pagefile.Format(desired).SequenceEqual(Pagefile.Format(_loadedPagefile)))
                Try(messages, "Paginação", () =>
                {
                    Pagefile.Write(desired);
                    _services.Restart.Changed(KeyPagefile, "paginação", string.Join("|", Pagefile.Format(_loadedPagefile)), string.Join("|", Pagefile.Format(desired)));
                    Log.Info($"Paginação: {string.Join(", ", Pagefile.Format(desired).DefaultIfEmpty("desligada"))} (vale ao reiniciar)");
                });

            if (CompressionKnown && MemoryCompression != _loadedCompression)
                Try(messages, "Memória compactada", () =>
                {
                    if (Compression.Set(MemoryCompression) is { } error) throw new InvalidOperationException(error);
                    _services.Restart.Changed(KeyCompression, "memória compactada", (_loadedCompression ?? false).ToString(), MemoryCompression.ToString());
                    Log.Info($"Memória compactada {(MemoryCompression ? "ligada" : "desligada")} (vale ao reiniciar)");
                });

            if (GameMode != before.GameMode) Try(messages, "Modo de Jogo", () => GamingSettings.SetGameMode(GameMode));
            if (WindowedOptimizations != before.WindowedOptimizations) Try(messages, "Otimizações de janela", () => GamingSettings.SetWindowedOptimizations(WindowedOptimizations));
            if (GameBarCapture != before.GameBarCapture) Try(messages, "Game Bar", () => GamingSettings.SetGameBarCapture(GameBarCapture));
            if (HardwareGpuScheduling != (before.HardwareGpuScheduling ?? false))
                Try(messages, "Agendamento de GPU", () =>
                {
                    GamingSettings.SetHardwareGpuScheduling(HardwareGpuScheduling);
                    _services.Restart.Changed(KeyHags, "agendamento de GPU", (before.HardwareGpuScheduling?.ToString() ?? "padrão"), HardwareGpuScheduling.ToString());
                    Log.Info($"Agendamento de GPU {(HardwareGpuScheduling ? "ligado" : "desligado")} (vale ao reiniciar)");
                });
        });

        if (PowerScheme != _services.Engine.Config.Power.SchemeDuringGame)
        {
            _services.Engine.UpdatePower(new PowerSettings { SchemeDuringGame = PowerScheme });
            _services.SaveEngineConfig();
        }

        Status = messages.Count == 0 ? "Aplicado." : string.Join(" · ", messages);
        await LoadAsync();
    }

    private static void Try(List<string> messages, string what, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            messages.Add(ex is UnauthorizedAccessException or System.Security.SecurityException
                ? $"{what}: precisa do Folga como administrador"
                : $"{what}: {ex.Message}");
            Log.Warn($"{what}: {ex.Message}");
        }
    }

    private void ShowRestart()
    {
        var pending = _services.Restart.PendingLabels;
        RestartPending = pending.Count > 0;
        RestartText = RestartPending ? $"Reinicie o PC para valer: {string.Join(", ", pending)}." : "";
    }

    /// <summary>Abre o diálogo padrão do Windows de reiniciar; quem confirma e reinicia é o próprio Windows.</summary>
    [RelayCommand]
    private void Restart()
    {
        if (Application.Current?.MainWindow is { } window && !RestartPrompt.Show(window)) Status = "Reinício adiado. As mudanças valem quando o PC reiniciar.";
    }

    private static string Gb(long bytes) => (bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture);
}
