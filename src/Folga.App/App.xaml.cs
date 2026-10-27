using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Folga.App.Services;
using Folga.App.ViewModels;
using Folga.Core.Logging;
using Folga.Core.Native;
using H.NotifyIcon;

namespace Folga.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppServices? _services;
    private MainWindow? _window;
    private TaskbarIcon? _tray;
    private MenuItem? _boostItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Global\Folga.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("O Folga já está aberto. Procure o ícone na bandeja.", "Folga");
            Shutdown();
            return;
        }

        Log.Init(Log.DefaultDirectory);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error($"Erro não tratado na interface: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error($"Erro fatal: {args.ExceptionObject}");
        SessionEnding += (_, _) => _services?.Dispose(); // logoff/desligar: desfaz antes de sair

        Log.Info($"Folga {typeof(App).Assembly.GetName().Version} iniciando");
        var missing = Privileges.Enable(Privileges.Required);
        if (missing.Count > 0) Log.Warn($"Privilégios não disponíveis: {string.Join(", ", missing)}");

        // --config <arquivo>: perfis de teste sem mexer no config.json do dia a dia.
        var configIndex = Array.FindIndex(e.Args, a => a.Equals("--config", StringComparison.OrdinalIgnoreCase));
        _services = AppServices.Create(configIndex >= 0 && configIndex + 1 < e.Args.Length ? e.Args[configIndex + 1] : null);
        _services.Engine.Start();

        var viewModel = new MainViewModel(_services);
        _window = new MainWindow { DataContext = viewModel };
        CreateTray(viewModel);
        _services.Engine.StateChanged += () => Dispatcher.BeginInvoke(UpdateTray);
        UpdateTray();

        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) ShowWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        Log.Info("Folga encerrado");
        base.OnExit(e);
    }

    private void CreateTray(MainViewModel viewModel)
    {
        _boostItem = new MenuItem { Header = "Boost ligado", IsCheckable = true };
        _boostItem.Click += (_, _) =>
        {
            _services!.Engine.SetEnabled(_boostItem.IsChecked);
            _services.SaveEngineConfig();
        };
        var open = new MenuItem { Header = "Abrir o Folga" };
        open.Click += (_, _) => ShowWindow();
        var restore = new MenuItem { Header = "Restaurar tudo" };
        restore.Click += (_, _) => viewModel.RestoreAllCommand.Execute(null);
        var exit = new MenuItem { Header = "Sair" };
        exit.Click += (_, _) =>
        {
            if (_window is not null) _window.AllowClose = true;
            Shutdown();
        };

        _tray = new TaskbarIcon
        {
            ToolTipText = "Folga",
            IconSource = new GeneratedIconSource
            {
                Text = "F",
                Foreground = (Brush)FindResource("AccentBrush"),
                Background = (Brush)FindResource("BackgroundBrush"),
                FontWeight = FontWeights.Bold,
            },
            ContextMenu = new ContextMenu { Items = { open, _boostItem, restore, new Separator(), exit } },
            DoubleClickCommand = new RelayCommand(ShowWindow),
        };
        // Sem modo de eficiência: com o jogo usando toda a CPU, o Folga precisa responder rápido aos eventos.
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private void UpdateTray()
    {
        if (_services is null || _tray is null || _boostItem is null) return;
        var state = _services.Engine.GetState();
        _boostItem.IsChecked = state.Enabled;
        _tray.ToolTipText = !state.Enabled ? "Folga · boost desligado"
            : state.SessionActive ? $"Folga · {state.Games[0].ProfileName} · {state.HeldProcesses} apps segurados"
            : "Folga · esperando um jogo";
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}
