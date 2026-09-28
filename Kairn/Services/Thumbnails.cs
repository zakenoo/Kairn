using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace Kairn.Services;

/// <summary>
/// Miniatures des images de la bibliothèque. Avant, chaque affichage de l'onglet redécodait toutes les images en entier,
/// sur le fil de l'interface : une demi-seconde de gel pour trente captures d'écran, à chaque retour sur l'onglet.
/// Maintenant, chaque miniature (320 px de large) est fabriquée une seule fois et gardée dans data\cache\thumbs,
/// puis relue en arrière-plan. La réparation des captures « transparentes » (voir ImageFix) se fait à ce moment-là,
/// une fois par fichier.
/// </summary>
public static class Thumbnails
{
    private const int Width = 320;
    private static string Dir => Path.Combine(Storage.Root, "cache", "thumbs");

    /// <summary>Miniatures encore en mémoire (affichées, ou pas encore libérées) : un nouvel affichage les reprend telles quelles.</summary>
    private static readonly ConcurrentDictionary<string, WeakReference<BitmapSource>> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, object> Locks = new();

    /// <summary>Miniature déjà en mémoire, sans rien lire sur le disque (null sinon).</summary>
    public static BitmapSource? Cached(string itemId, string path) =>
        ThumbFile(itemId, path) is { } file && Loaded.TryGetValue(file, out var weak) && weak.TryGetTarget(out var img) ? img : null;

    /// <summary>Miniature d'une image, lue sur le disque ou fabriquée la première fois. À appeler hors du fil de l'interface.</summary>
    public static BitmapSource? Get(string itemId, string path)
    {
        lock (Locks.GetOrAdd(itemId, _ => new object()))
        {
            try
            {
                if (ThumbFile(itemId, path) is not { } file) return null;
                if (Loaded.TryGetValue(file, out var weak) && weak.TryGetTarget(out var hit)) return hit;

                var img = File.Exists(file) ? Load(file) : null;
                if (img is null)
                {
                    // Capture collée avant la correction (transparente partout) : réparée une fois pour toutes.
                    // Le fichier a alors changé, donc sa miniature aussi.
                    if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && ImageFix.RepairFile(path))
                        file = ThumbFile(itemId, path) ?? file;
                    img = Decode(path);
                    Save(img, itemId, file);
                }
                Loaded[file] = new WeakReference<BitmapSource>(img);
                return img;
            }
            catch { return null; }
        }
    }

    /// <summary>Nom de la miniature : l'élément, puis la date et la taille de l'image (une image remplacée en change).</summary>
    private static string? ThumbFile(string itemId, string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? Path.Combine(Dir, $"{itemId}-{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}.png") : null;
    }

    private static BitmapSource Decode(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri(path);
        img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        img.DecodePixelWidth = Width;
        img.CacheOption = BitmapCacheOption.OnLoad; // fichier non verrouillé
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static BitmapSource? Load(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch { return null; }
    }

    private static void Save(BitmapSource img, string itemId, string file)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            foreach (var old in Directory.GetFiles(Dir, itemId + "-*.png")) File.Delete(old); // image remplacée depuis
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using (var fs = File.Create(file + ".tmp")) enc.Save(fs);
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch { /* pas gardée cette fois : elle sera refaite au prochain affichage */ }
    }

    /// <summary>Retire les miniatures des images qui ne sont plus dans la bibliothèque. À appeler hors du fil de l'interface.</summary>
    public static void Sweep(IReadOnlySet<string> itemIds)
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir))
            {
                var name = Path.GetFileName(f);
                int dash = name.IndexOf('-');
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || dash <= 0 || !itemIds.Contains(name[..dash]))
                    try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
