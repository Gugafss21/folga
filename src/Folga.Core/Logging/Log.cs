namespace Folga.Core.Logging;

/// <summary>Log simples: arquivo diário em %LocalAppData%\Folga\logs e as últimas linhas em memória para a tela.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly LinkedList<string> Recent = new();
    private const int RecentLimit = 300;
    private static string? _directory;

    public static event Action<string>? LineWritten;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Folga", "logs");

    /// <summary>Sem chamar Init, o log fica só em memória (útil nos testes).</summary>
    public static void Init(string directory)
    {
        Directory.CreateDirectory(directory);
        _directory = directory;
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERRO", message);

    public static IReadOnlyList<string> RecentLines()
    {
        lock (Gate) return Recent.ToList();
    }

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss} {level} {message}";
        lock (Gate)
        {
            Recent.AddLast(line);
            if (Recent.Count > RecentLimit) Recent.RemoveFirst();
            if (_directory is not null)
            {
                try { File.AppendAllText(Path.Combine(_directory, $"folga-{now:yyyy-MM-dd}.log"), line + Environment.NewLine); }
                catch (IOException) { }
            }
        }
        LineWritten?.Invoke(line);
    }
}
