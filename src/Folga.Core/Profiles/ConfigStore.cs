using System.Text.Json;
using System.Text.Json.Serialization;
using Folga.Core.Logging;

namespace Folga.Core.Profiles;

/// <summary>Perfis e regras em JSON (%AppData%\Folga\config.json).</summary>
public sealed class ConfigStore(string path)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Folga", "config.json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = path;

    public FolgaConfig Load()
    {
        if (!File.Exists(FilePath))
        {
            var config = DefaultConfig.Create();
            Save(config);
            return config;
        }
        try
        {
            return JsonSerializer.Deserialize<FolgaConfig>(File.ReadAllText(FilePath), Json) ?? DefaultConfig.Create();
        }
        catch (JsonException ex)
        {
            // Não sobrescreve o arquivo do usuário: guarda uma cópia e segue com o padrão.
            var backup = FilePath + ".invalido";
            File.Copy(FilePath, backup, overwrite: true);
            Log.Error($"config.json inválido ({ex.Message}); cópia em {backup}, usando o padrão");
            return DefaultConfig.Create();
        }
    }

    public void Save(FolgaConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(config, Json));
    }

    public static FolgaConfig Clone(FolgaConfig config) =>
        JsonSerializer.Deserialize<FolgaConfig>(JsonSerializer.Serialize(config, Json), Json)!;
}

internal static class AtomicFile
{
    /// <summary>Escreve num arquivo temporário, força para o disco e troca de uma vez.</summary>
    public static void WriteAllText(string path, string content)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
