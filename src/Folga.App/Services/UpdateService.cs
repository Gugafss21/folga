using Folga.Core.Engine;
using Folga.Core.Logging;
using Folga.Core.SystemSettings;

namespace Folga.App.Services;

/// <summary>
/// Procura versão nova no GitHub 1 min depois de abrir e a cada 12 h (se ligado na tela Sistema). Nunca
/// instala sozinho: avisa, e a pessoa atualiza com um clique quando não houver jogo aberto.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private readonly BoostEngine _engine;
    private Timer? _timer;
    private int _checking;

    public UpdateService(BoostEngine engine) => _engine = engine;

    public Updater Updater { get; } = new();

    public UpdateCheck? Last { get; private set; }

    /// <summary>Disparado na thread da verificação.</summary>
    public event Action<UpdateCheck>? Checked;

    public void Start() => _timer = new Timer(_ => _ = CheckIfEnabled(), null, FirstCheck, Interval);

    private async Task CheckIfEnabled()
    {
        // Com jogo aberto, deixa para a próxima: nem a rede o Folga disputa com o jogo.
        if (!_engine.Config.Updates.AutoCheck || _engine.GetState().SessionActive) return;
        await CheckNowAsync();
    }

    public async Task<UpdateCheck> CheckNowAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return Last ?? new UpdateCheck(UpdateStatus.Error, Message: "já procurando");
        try
        {
            var check = await Updater.CheckAsync();
            if (check.Status == UpdateStatus.Available && check.Info!.Version != Last?.Info?.Version)
                Log.Info($"Atualização disponível: Folga {check.Info.Version}");
            else if (check.Status == UpdateStatus.Error)
                Log.Warn($"Não deu para procurar atualização: {check.Message}");
            Last = check;
            Checked?.Invoke(check);
            return check;
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
