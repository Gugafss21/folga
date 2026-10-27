using System.Text.Json;
using Folga.Core.Logging;
using Folga.Core.Processes;
using Folga.Core.Profiles;

namespace Folga.Core.Journal;

public sealed class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ChangeKind Kind { get; set; }
    public int Pid { get; set; }
    public string ExeName { get; set; } = "";
    public long StartTime { get; set; }
    public long[] Original { get; set; } = [];
    public long[] Applied { get; set; } = [];
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;

    public ProcessRef Process => new(Pid, ExeName, StartTime);
    public bool Is(ProcessRef p) => Pid == p.Pid && StartTime == p.StartTime;
}

/// <summary>
/// Diário de alterações (%ProgramData%\Folga\journal.json). Toda alteração é gravada em disco
/// antes de ser aplicada, para que um crash do app nunca deixe o PC "preso".
/// </summary>
public sealed class ChangeJournal
{
    private readonly object _gate = new();
    private readonly List<JournalEntry> _entries;

    public string FilePath { get; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Folga", "journal.json");

    public ChangeJournal(string path)
    {
        FilePath = path;
        _entries = LoadFrom(path);
    }

    public IReadOnlyList<JournalEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public bool Has(ProcessRef process, ChangeKind kind)
    {
        lock (_gate) return _entries.Any(e => e.Kind == kind && e.Is(process));
    }

    public JournalEntry? FindIfeo(string exeName)
    {
        lock (_gate)
            return _entries.FirstOrDefault(e => e.Kind == ChangeKind.Ifeo && string.Equals(e.ExeName, exeName, StringComparison.OrdinalIgnoreCase));
    }

    public void AddRange(IReadOnlyCollection<JournalEntry> entries)
    {
        if (entries.Count == 0) return;
        lock (_gate)
        {
            _entries.AddRange(entries);
            Save();
        }
    }

    public void Add(JournalEntry entry) => AddRange([entry]);

    public int RemoveWhere(Func<JournalEntry, bool> predicate)
    {
        lock (_gate)
        {
            var removed = _entries.RemoveAll(e => predicate(e));
            if (removed > 0) Save();
            return removed;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_entries, ConfigStore.Json));
    }

    private static List<JournalEntry> LoadFrom(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(path), ConfigStore.Json) ?? [];
        }
        catch (JsonException ex)
        {
            Log.Error($"journal.json ilegível ({ex.Message}); começando vazio");
            return [];
        }
    }
}
