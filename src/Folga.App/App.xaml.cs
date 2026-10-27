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
    private const string ShowSignalName = @"Local\Folga.Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
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
            // Abrir de novo (atalho, Menu Iniciar) traz a janela do Folga que já está na bandeja.
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
            {
                AllowSetForegroundWindow(-1); // ASFW_ANY: deixa a outra instância vir para a frente
                using (signal) signal.Set();
            }
            else
            {
                MessageBox.Show("O Folga já está aberto (em outra sessão do Windows). Procure o ícone na bandeja.", "Folga");
            }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        Log.Init(Log.DefaultDirectory);

        // Dicas: aparecem rápido e ficam na tela enquanto o mouse estiver em cima (o padrão some em 5 s).
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(400));
        ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(int.MaxValue));

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
        _services.Detector.Start();
        _services.Overlay.Start();

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
        _showSignal?.Dispose();
        _singleInstance?.Dispose();
        Log.Info("Folga encerrado");
        base.OnExit(e);
    }

    private void CreateTray(MainViewModel viewModel)
    {
        _boostItem = new MenuItem
        {
            Header = "Boost ligado", IsCheckable = true,
            ToolTip = "Marcado: quando um jogo da lista abre, ele ganha prioridade e os apps de fundo são segurados. Desmarcar desfaz na hora.",
        };
        _boostItem.Click += (_, _) =>
        {
            _services!.Engine.SetEnabled(_boostItem.IsChecked);
            _services.SaveEngineConfig();
        };
        var open = new MenuItem { Header = "Abrir o Folga", ToolTip = "Mostra a janela principal. Dois cliques no ícone fazem o mesmo." };
        open.Click += (_, _) => ShowWindow();
        var restore = new MenuItem { Header = "Restaurar tudo", ToolTip = "Desfaz tudo o que o Folga mudou, inclusive o registro (IFEO), e desliga o boost. Pede confirmação." };
        restore.Click += (_, _) => viewModel.RestoreAllCommand.Execute(null);
        var exit = new MenuItem { Header = "Sair", ToolTip = "Fecha o Folga. O que estiver aplicado é desfeito antes de sair. Fechar a janela no X só esconde na bandeja." };
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

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}
