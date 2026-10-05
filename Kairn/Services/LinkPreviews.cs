using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Kairn.Services;

/// <summary>
/// Aperçu d'un lien de la bibliothèque : l'image que la page annonce elle-même (balise og:image, celle que montrent
/// les messageries quand on y colle un lien). Elle est demandée au site, sans cookies, une seule fois, puis gardée
/// dans data\cache\previews en petite taille. Un lien sans image garde sa carte habituelle ; on ne réessaie qu'une semaine plus tard.
/// </summary>
public static partial class LinkPreviews
{
    private const int Width = 320;
    // Pinterest place sa balise og:image vers 1,3 Mo dans la page : on lit assez loin, mais on s'arrête dès qu'on l'a.
    private const int MaxPage = 3 * 1024 * 1024, MaxImage = 10 * 1024 * 1024;
    private static string Dir => Path.Combine(Storage.Root, "cache", "previews");

    private static readonly ConcurrentDictionary<string, Task<Preview>> Running = new();
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
            { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Kairn");
        return h;
    });

    /// <summary>
    /// Ce qu'on a appris du lien. <see cref="Title"/> n'est rempli que lorsque la page vient d'être demandée
    /// (pas quand l'image est relue depuis le cache) : la carte ne reprend le titre qu'une fois.
    /// </summary>
    public record Preview(BitmapSource? Image, string? Title);

    private static readonly Preview Nothing = new(null, null);

    /// <summary>Aperçu du lien : lu dans le cache, ou demandé au site la première fois.</summary>
    public static Task<Preview> GetAsync(string itemId, string url)
    {
        if (!Launcher.IsUrl(url)) return Task.FromResult(Nothing);
        var file = Path.Combine(Dir, $"{itemId}-{Hash(url.Trim())}");
        // Deux cartes du même lien (ou un retour rapide sur l'onglet) attendent la même demande.
        if (Running.TryGetValue(file, out var running)) return running!;
        var task = Task.Run(() => LoadOrFetchAsync(file, url.Trim()));
        Running[file] = task;
        task.ContinueWith(_ => Running.TryRemove(file, out Task<Preview>? _), TaskScheduler.Default);
        return task;
    }

    private static async Task<Preview> LoadOrFetchAsync(string file, string url)
    {
        var jpg = file + ".jpg";
        if (File.Exists(jpg))
        {
            try
            {
                using var fs = File.OpenRead(jpg);
                return new Preview(Thumbnails.Read(fs), null);
            }
            catch { /* fichier abîmé : on le refait */ }
        }
        var none = file + ".none";
        if (File.Exists(none) && DateTime.Now - File.GetLastWriteTime(none) < TimeSpan.FromDays(7)) return Nothing;

        BitmapSource? img = null;
        string? title = null;
        try { (img, title) = await DownloadAsync(new Uri(url)); } catch { }
        try
        {
            Directory.CreateDirectory(Dir);
            foreach (var old in Directory.GetFiles(Dir, Path.GetFileName(file).Split('-')[0] + "-*")) File.Delete(old); // adresse modifiée depuis
            if (img is null)
            {
                File.WriteAllBytes(none, []);
                return new Preview(null, title);
            }
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(img));
            using (var fs = File.Create(jpg + ".tmp")) enc.Save(fs);
            File.Move(jpg + ".tmp", jpg, overwrite: true);
        }
        catch { /* pas gardé cette fois : redemandé au prochain affichage */ }
        return new Preview(img, title);
    }

    /// <summary>La page, puis l'image qu'elle annonce, et son titre. Un lien qui mène directement à une image sert tel quel.</summary>
    private static async Task<(BitmapSource? Image, string? Title)> DownloadAsync(Uri page)
    {
        using var res = await Http.Value.GetAsync(page, HttpCompletionOption.ResponseHeadersRead);
        if (!res.IsSuccessStatusCode) return (null, null);
        var type = res.Content.Headers.ContentType?.MediaType ?? "";
        if (type.StartsWith("image/")) return (Decode(await ReadAsync(res, MaxImage, stopAfter: null)), null);
        if (type != "text/html") return (null, null);

        var html = Encoding.UTF8.GetString(await ReadAsync(res, MaxPage, stopAfter: "og:image"u8.ToArray()));
        var metas = Metas(html);
        var title = PageTitle(html, metas);
        foreach (var image in ImageLinks(metas, res.RequestMessage?.RequestUri ?? page).Take(3))
        {
            try
            {
                using var imgRes = await Http.Value.GetAsync(image, HttpCompletionOption.ResponseHeadersRead);
                if (!imgRes.IsSuccessStatusCode) continue;
                if (Decode(await ReadAsync(imgRes, MaxImage, stopAfter: null)) is { } img) return (img, title);
            }
            catch { /* suivante */ }
        }
        return (null, title);
    }

    /// <summary>Lit la réponse jusqu'à <paramref name="max"/> octets, ou un peu après <paramref name="stopAfter"/> s'il apparaît.</summary>
    private static async Task<byte[]> ReadAsync(HttpResponseMessage res, int max, byte[]? stopAfter)
    {
        await using var stream = await res.Content.ReadAsStreamAsync();
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long foundAt = -1;
        int n;
        while ((n = await stream.ReadAsync(chunk)) > 0)
        {
            long before = buffer.Length;
            buffer.Write(chunk, 0, n);
            if (buffer.Length > max) return [];
            if (stopAfter is null) continue;
            if (foundAt < 0)
            {
                // Le mot cherché peut être coupé entre deux morceaux : on reprend un peu avant.
                int from = (int)Math.Max(0, before - stopAfter.Length);
                int i = buffer.GetBuffer().AsSpan(from, (int)buffer.Length - from).IndexOf(stopAfter);
                if (i >= 0) foundAt = from + i;
            }
            if (foundAt >= 0 && buffer.Length > foundAt + 8192) break; // la balise est arrivée, et le titre qui la suit d'habitude
        }
        return buffer.ToArray();
    }

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))")]
    private static partial Regex Attribute();

    /// <summary>Les balises &lt;meta property/name="…" content="…"&gt; de la page, dans l'ordre.</summary>
    private static List<(string Key, string Content)> Metas(string html)
    {
        var found = new List<(string, string)>();
        foreach (Match tag in MetaTag().Matches(html))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in Attribute().Matches(tag.Value))
                attrs[a.Groups[1].Value] = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
            var key = (attrs.GetValueOrDefault("property") ?? attrs.GetValueOrDefault("name") ?? "").ToLowerInvariant();
            if (key.Length > 0 && attrs.TryGetValue("content", out var content) && content.Trim().Length > 0) found.Add((key, content.Trim()));
        }
        return found;
    }

    /// <summary>Les images annoncées par la page (og:image d'abord, puis celles prévues pour Twitter).</summary>
    private static IEnumerable<Uri> ImageLinks(List<(string Key, string Content)> metas, Uri baseUri)
    {
        var found = new List<(Uri Uri, int Rank)>();
        foreach (var (key, content) in metas)
        {
            int rank = key switch { "og:image:secure_url" => 0, "og:image" or "og:image:url" => 1, "twitter:image" or "twitter:image:src" => 2, _ => -1 };
            if (rank < 0) continue;
            if (!Uri.TryCreate(baseUri, content, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) continue;
            found.Add((uri, rank));
        }
        return found.OrderBy(f => f.Rank).Select(f => f.Uri).Distinct();
    }

    [GeneratedRegex(@"<title\b[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// Titre de la page (og:title, sinon &lt;title&gt;), sans ce qui suit « | » : chez Pinterest, ce sont des mots-clés
    /// (« Proportions du corps | Anatomie, Croquis… ») ; ailleurs, souvent le nom du site.
    /// </summary>
    private static string? PageTitle(string html, List<(string Key, string Content)> metas)
    {
        var raw = metas.FirstOrDefault(m => m.Key is "og:title" or "twitter:title").Content;
        if (raw is null && TitleTag().Match(html) is { Success: true } t) raw = WebUtility.HtmlDecode(t.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var title = Spaces().Replace(raw, " ").Trim();
        int bar = title.IndexOf(" | ", StringComparison.Ordinal);
        if (bar >= 3) title = title[..bar].Trim();
        return title.Length > 120 ? title[..120].TrimEnd() + "…" : title;
    }

    /// <summary>Image ramenée à 320 px de large. Null si Windows ne sait pas la lire (SVG, AVIF…).</summary>
    private static BitmapSource? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try
        {
            var img = Thumbnails.Read(new MemoryStream(bytes), Width);
            return img.PixelWidth >= 16 && img.PixelHeight >= 16 ? img : null;
        }
        catch { return null; }
    }

    private static string Hash(string url) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..12].ToLowerInvariant();

    /// <summary>Retire les aperçus des liens qui ne sont plus dans la bibliothèque. À appeler hors du fil de l'interface.</summary>
    public static void Sweep(IReadOnlySet<string> itemIds)
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir))
            {
                var name = Path.GetFileName(f);
                int dash = name.IndexOf('-');
                if (dash <= 0 || !itemIds.Contains(name[..dash]))
                    try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
