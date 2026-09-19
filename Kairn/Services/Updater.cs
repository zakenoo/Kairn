using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kairn.Services;

/// <summary>
/// Mises à jour via les « Releases » GitHub du projet. Seulement si la personne l'a accepté :
/// Kairn demande à GitHub quelle est la dernière version, rien d'autre n'est envoyé.
/// </summary>
public static class Updater
{
    public const string Repo = "zakenoo/Kairn";

    public record Update(Version Version, string Url, string? Sha256, string Notes, long Size);

    /// <summary>Dernière mise à jour trouvée (null : à jour, ou pas encore vérifié).</summary>
    public static Update? Available { get; private set; }
    public static event Action? Changed;

    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Kairn-Updater");
        return h;
    }

    private static System.Windows.Threading.DispatcherTimer? _timer;

    /// <summary>Vérifie au démarrage puis une fois par jour, si l'option est activée.</summary>
    public static void StartAutoCheck()
    {
        if (_timer != null) { _ = CheckAsync(); return; }
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = CheckAsync();
    }

    /// <summary>Renvoie la mise à jour disponible, ou null. Ne lève pas d'exception sauf si <paramref name="manual"/>.</summary>
    public static async Task<Update?> CheckAsync(bool manual = false)
    {
        if (!manual && !Storage.Settings.CheckUpdates) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", cts.Token));
            var root = doc.RootElement;
            var tag = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var v) || v <= Normalize(Installer.CurrentVersion)) { Set(null); return null; }
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("Kairn-Setup", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } dig && dig.StartsWith("sha256:") ? dig[7..] : null;
                var up = new Update(v, a.GetProperty("browser_download_url").GetString()!, sha,
                                    root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", a.GetProperty("size").GetInt64());
                Set(up);
                return up;
            }
            Set(null);
            return null;
        }
        catch when (!manual) { return null; }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static void Set(Update? u)
    {
        Available = u;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    /// <summary>
    /// Télécharge le nouveau setup, vérifie son empreinte, puis le lance en mode mise à jour :
    /// il attend la fermeture de Kairn, remplace le fichier et relance l'app.
    /// </summary>
    public static async Task DownloadAndApplyAsync(Update u, IProgress<double> progress, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"Kairn-Setup-{Installer.VersionText(u.Version)}.exe");
        using (var resp = await Http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? u.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buf = new byte[1 << 20];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }
        if (u.Sha256 != null)
        {
            await using var fs = File.OpenRead(file);
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)), u.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                throw new InvalidDataException("checksum");
            }
        }
        // Le setup remplacera exactement le fichier en cours d'utilisation (installé, ou copie portable).
        Process.Start(new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            ArgumentList = { Installer.UpdateArg, Environment.ProcessId.ToString(), Environment.ProcessPath ?? Installer.InstalledExe },
        });
    }
}
