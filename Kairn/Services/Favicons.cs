using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kairn.Services;

/// <summary>
/// Icône d'un site ajouté à « Mes outils ». Elle est demandée au site lui-même, jamais à un service tiers
/// (ceux de Google et compagnie verraient passer la liste de tes outils), une seule fois, puis gardée dans
/// data\cache\icons. Un site sans icône lisible garde le pictogramme habituel ; on ne lui redemande qu'une semaine plus tard.
/// </summary>
public static partial class Favicons
{
    /// <summary>Assez grand pour une tuile de 30 px sur un écran agrandi.</summary>
    private const int Size = 64;
    private const int MaxPage = 256 * 1024, MaxIcon = 512 * 1024;
    private static string Dir => Path.Combine(Storage.Root, "cache", "icons");

    private static readonly Dictionary<string, ImageSource> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        // Pas de cookies : le site ne reconnaît pas la personne, il voit une simple demande d'icône.
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
            { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Kairn");
        return h;
    });

    /// <summary>Icône déjà connue pour cette adresse (null sinon). Aucune requête.</summary>
    public static ImageSource? Cached(string url)
    {
        foreach (var key in Keys(url))
        {
            if (Loaded.TryGetValue(key, out var img)) return img;
            var file = Path.Combine(Dir, key + ".png");
            if (!File.Exists(file)) continue;
            try
            {
                using var fs = File.OpenRead(file);
                var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                return Loaded[key] = frame;
            }
            catch { /* fichier abîmé : il sera refait */ }
        }
        return null;
    }

    /// <summary>
    /// Demande l'icône au site si on ne l'a pas encore (ni essayé depuis moins d'une semaine).
    /// Vrai si une nouvelle icône vient d'arriver (la page qui l'affiche se redessine alors).
    /// </summary>
    public static async Task<bool> FetchAsync(string url)
    {
        if (!Launcher.IsUrl(url) || Cached(url) != null) return false;
        var key = Keys(url).First();
        var none = Path.Combine(Dir, key + ".none");
        if (File.Exists(none) && DateTime.Now - File.GetLastWriteTime(none) < TimeSpan.FromDays(7)) return false;
        if (!Pending.Add(key)) return false;
        try
        {
            var png = await Task.Run(() => DownloadAsync(new Uri(url.Trim())));
            Directory.CreateDirectory(Dir);
            if (png is null)
            {
                File.WriteAllBytes(none, []);
                return false;
            }
            File.WriteAllBytes(Path.Combine(Dir, key + ".png"), png);
            if (File.Exists(none)) File.Delete(none);
            return Cached(url) != null;
        }
        catch { return false; }
        finally { Pending.Remove(key); }
    }

    /// <summary>
    /// Clés de rangement : le site et la première partie du chemin d'abord (Google Docs et Google Sheets n'ont pas la même icône),
    /// puis le site seul (une vidéo YouTube reprend l'icône de YouTube).
    /// </summary>
    private static IEnumerable<string> Keys(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Host.Length == 0) yield break;
        var host = u.IdnHost.ToLowerInvariant(); // forme ASCII : utilisable comme nom de fichier
        if (host.StartsWith("www.")) host = host[4..];
        var segment = u.AbsolutePath.Trim('/').Split('/')[0].ToLowerInvariant();
        segment = SafeChars().Replace(segment, "");
        if (segment.Length > 0) yield return $"{host}_{segment}";
        yield return host;
    }

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex SafeChars();

    /// <summary>Les icônes que la page annonce (souvent plus grandes et plus nettes), puis l'emplacement classique /favicon.ico.</summary>
    private static async Task<byte[]?> DownloadAsync(Uri page)
    {
        var candidates = new List<Uri>();
        try
        {
            using var res = await Http.Value.GetAsync(page, HttpCompletionOption.ResponseHeadersRead);
            if (res.IsSuccessStatusCode && res.Content.Headers.ContentType?.MediaType == "text/html")
            {
                var html = Encoding.UTF8.GetString(await ReadAsync(res, MaxPage));
                candidates.AddRange(IconLinks(html, res.RequestMessage?.RequestUri ?? page));
            }
        }
        catch { /* page injoignable : il reste /favicon.ico */ }
        candidates.Add(new Uri(page, "/favicon.ico"));

        foreach (var icon in candidates.Distinct().Take(5))
        {
            try
            {
                using var res = await Http.Value.GetAsync(icon, HttpCompletionOption.ResponseHeadersRead);
                if (!res.IsSuccessStatusCode) continue;
                if (ToPng(await ReadAsync(res, MaxIcon)) is { } png) return png;
            }
            catch { /* suivante */ }
        }
        return null;
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage res, int max)
    {
        await using var stream = await res.Content.ReadAsStreamAsync();
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > max) break; // assez : l'en-tête de la page, ou une icône raisonnable
        }
        return buffer.ToArray();
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"([\w-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))")]
    private static partial Regex Attribute();

    /// <summary>
    /// Les balises &lt;link rel="icon"&gt; de la page, de la plus adaptée à la moins adaptée :
    /// la plus petite d'au moins 64 px, sinon la plus grande. Les SVG sont laissés de côté (WPF ne les lit pas).
    /// </summary>
    private static IEnumerable<Uri> IconLinks(string html, Uri baseUri)
    {
        var found = new List<(Uri Uri, int Px)>();
        foreach (Match tag in LinkTag().Matches(html))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in Attribute().Matches(tag.Value))
                attrs[a.Groups[1].Value] = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
            var rel = attrs.GetValueOrDefault("rel", "").ToLowerInvariant();
            if (!rel.Contains("icon") || rel.Contains("mask-icon")) continue;
            if (!attrs.TryGetValue("href", out var href) || href.Length == 0 || href.StartsWith("data:")) continue;
            if (href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || attrs.GetValueOrDefault("type", "").Contains("svg")) continue;
            if (!Uri.TryCreate(baseUri, href, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) continue;
            int px = rel.Contains("apple-touch-icon") ? 180 : 32;
            if (attrs.TryGetValue("sizes", out var sizes) && int.TryParse(sizes.Split('x', 'X')[0], out var s)) px = s;
            found.Add((uri, px));
        }
        return found.OrderBy(f => f.Px >= Size ? f.Px - Size : 10_000 - f.Px).Select(f => f.Uri);
    }

    /// <summary>Image lisible par Windows (ICO, PNG, JPEG, GIF…) ramenée à 64 px au plus, en PNG. Null si ce n'est pas une image.</summary>
    private static byte[]? ToPng(byte[] bytes)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            // Un .ico contient souvent plusieurs tailles : on garde la plus grande.
            BitmapSource frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            if (frame.PixelWidth < 8 || frame.PixelHeight < 8) return null;
            double scale = Math.Min(1, (double)Size / Math.Max(frame.PixelWidth, frame.PixelHeight));
            if (scale < 1) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(frame));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
