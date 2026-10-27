using System.Text.Json;
using Folga.Core.Profiles;

namespace Folga.Core.SystemSettings;

/// <summary>
/// Ajustes que só valem depois de reiniciar (%ProgramData%\Folga\restart.json). Guarda o valor que cada
/// um tinha neste boot: voltar a ele antes de reiniciar tira o aviso; depois de reiniciar, a lista zera.
/// </summary>
public sealed class RestartTracker
{
    private static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    private sealed class Pending
    {
        public string Label { get; set; } = "";
        public string BootValue { get; set; } = "";
    }

    private sealed class State
    {
        public DateTime BootUtc { get; set; }
        public Dictionary<string, Pending> Items { get; set; } = [];
    }

    private readonly object _gate = new();
    private readonly string _path;
    private readonly DateTime _bootUtc;
    private State _state;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Folga", "restart.json");

    public static DateTime CurrentBootUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public RestartTracker(string path, DateTime? bootUtc = null)
    {
        _path = path;
        _bootUtc = bootUtc ?? CurrentBootUtc;
        _state = Load();
        if ((_state.BootUtc - _bootUtc).Duration() > BootTolerance) _state = new State { BootUtc = _bootUtc }; // reiniciou: tudo aplicado
    }

    /// <summary>Nomes dos ajustes esperando o reinício, na ordem em que mudaram.</summary>
    public IReadOnlyList<string> PendingLabels
    {
        get { lock (_gate) return _state.Items.Values.Select(i => i.Label).ToList(); }
    }

    /// <summary>
    /// Registra a mudança de um ajuste de <paramref name="before"/> para <paramref name="after"/>. Se voltou ao
    /// valor deste boot, deixa de estar pendente.
    /// </summary>
    public void Changed(string key, string label, string before, string after)
    {
        lock (_gate)
        {
            if (!_state.Items.TryGetValue(key, out var item)) _state.Items[key] = item = new Pending { Label = label, BootValue = before };
            if (item.BootValue == after) _state.Items.Remove(key);
            Save();
        }
    }

    private State Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_path), ConfigStore.Json) ?? new State { BootUtc = _bootUtc } : new State { BootUtc = _bootUtc };
        }
        catch (JsonException)
        {
            return new State { BootUtc = _bootUtc };
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_state, ConfigStore.Json));
    }
}
