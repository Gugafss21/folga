using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Folga.Core.SystemSettings;

/// <summary>ChecksumUrl: o Folga.exe.sha256 da release, usado quando o GitHub não traz o digest do .exe.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string? Sha256, string Notes, string? ChecksumUrl = null);

public enum UpdateStatus { UpToDate, Available, NoReleases, Error }

public sealed record UpdateCheck(UpdateStatus Status, UpdateInfo? Info = null, string? Message = null);

/// <summary>
/// Atualização pelo GitHub Releases do Folga: a release mais nova traz o Folga.exe e o hash SHA-256 dele
/// (o "digest" que o GitHub calcula, ou um Folga.exe.sha256 publicado junto). Sem hash conferido, não instala.
/// </summary>
public sealed class Updater(string owner = Updater.Owner, string repo = Updater.Repo)
{
    public const string Owner = "Gugafss21";
    public const string Repo = "folga";
    private const string AssetName = "Folga.exe";

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public string ReleasesPage => $"https://github.com/{owner}/{repo}/releases";

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = Client();
            using var response = await http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new UpdateCheck(UpdateStatus.NoReleases, Message: "nenhuma versão publicada ainda");
            if (!response.IsSuccessStatusCode) return new UpdateCheck(UpdateStatus.Error, Message: $"GitHub respondeu {(int)response.StatusCode}");

            var info = Parse(await response.Content.ReadAsStringAsync(ct), Current);
            if (info is null) return new UpdateCheck(UpdateStatus.UpToDate);
            if (info.Sha256 is null && info.ChecksumUrl is { } url)
                info = info with { Sha256 = ParseChecksum(await http.GetStringAsync(url, ct)) };
            return new UpdateCheck(UpdateStatus.Available, info);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new UpdateCheck(UpdateStatus.Error, Message: ex switch
            {
                TaskCanceledException => "sem resposta do GitHub",
                KeyNotFoundException or InvalidOperationException => "resposta inesperada do GitHub", // JSON sem os campos de uma release
                _ => ex.Message,
            });
        }
    }

    /// <summary>A release, se for mais nova que <paramref name="current"/> e tiver o Folga.exe; senão null.</summary>
    public static UpdateInfo? Parse(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= current) return null;

        string? url = null, sha = null, checksumUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
            {
                url = asset.GetProperty("browser_download_url").GetString();
                if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = d["sha256:".Length..];
            }
            else if (string.Equals(name, AssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = asset.GetProperty("browser_download_url").GetString();
            }
        }
        if (url is null) return null;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, url, sha?.ToLowerInvariant(), notes, checksumUrl);
    }

    /// <summary>"hash  Folga.exe" (formato do sha256sum) ou só o hash.</summary>
    public static string? ParseChecksum(string text)
    {
        var first = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: 64 } && first.All(Uri.IsHexDigit) ? first.ToLowerInvariant() : null;
    }

    /// <summary>Onde a versão nova é baixada: ao lado do .exe atual.</summary>
    public static string DownloadPath(string exePath) => Path.ChangeExtension(exePath, ".new.exe");

    /// <summary>
    /// Baixa a versão nova para <see cref="DownloadPath"/> e confere o hash; um download já feito com o hash
    /// certo é reaproveitado. Download interrompido ou adulterado é apagado. Trocar fica com <see cref="Apply"/>.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, string exePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (info.Sha256 is null) throw new InvalidOperationException("a release não tem o hash SHA-256 do Folga.exe; por segurança, não instalo");
        var downloaded = DownloadPath(exePath);
        if (File.Exists(downloaded) && Sha256Of(downloaded).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) return downloaded;
        try
        {
            using var http = Client();
            using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(downloaded);
            var buffer = new byte[81920];
            long done = 0;
            int read, percent = -1;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0 && (int)(done * 100 / total.Value) is var now && now != percent)
                {
                    percent = now; // só a cada ponto percentual, não a cada bloco
                    progress?.Report(now / 100.0);
                }
            }
        }
        catch
        {
            TryDelete(downloaded);
            throw;
        }
        Verify(downloaded, info.Sha256);
        return downloaded;
    }

    /// <summary>
    /// Confere o hash de novo e troca: o .exe em uso vira .old.exe (o Windows deixa renomear um .exe aberto,
    /// não apagar) e o novo toma o lugar. Hash errado: apaga o baixado e não mexe em nada. Se o novo não
    /// entrar (antivírus ainda examinando, por exemplo), o antigo volta, para nunca ficar sem Folga.exe.
    /// </summary>
    public static void Apply(string downloaded, string expectedSha256, string exePath, int attempts = 10)
    {
        Verify(downloaded, expectedSha256);
        var old = Path.ChangeExtension(exePath, ".old.exe");
        File.Move(exePath, old, overwrite: true);
        try
        {
            for (var i = 1; ; i++)
            {
                try
                {
                    File.Move(downloaded, exePath);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && i < attempts)
                {
                    Thread.Sleep(300);
                }
            }
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
    }

    private static void Verify(string downloaded, string expectedSha256)
    {
        var actual = Sha256Of(downloaded);
        if (actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) return;
        File.Delete(downloaded);
        throw new InvalidDataException($"o arquivo baixado não confere com o hash publicado (esperado {expectedSha256[..12]}…, veio {actual[..12]}…)");
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Folga", Current.ToString())); // a API do GitHub exige User-Agent
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
