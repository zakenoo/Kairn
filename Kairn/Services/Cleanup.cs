using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Ménage discret, une fois par lancement et en arrière-plan, de ce que Kairn laissait derrière lui :
/// le setup téléchargé pour une mise à jour (plus de 200 Mo dans le dossier temporaire, jamais effacé),
/// les bibliothèques natives que .NET extrait pour chaque version (8 Mo par version, dans %TEMP%\.net),
/// et les miniatures des images retirées de la bibliothèque.
/// Rien n'est touché tant que ça sert encore (une autre version de Kairn ouverte, par exemple).
/// </summary>
public static class Cleanup
{
    /// <summary>Lance le ménage une demi-minute après le démarrage, quand tout est calme.</summary>
    public static void Schedule()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Relevé ici, sur le fil de l'interface : les données ne se lisent pas depuis un autre fil.
            var items = Storage.Data.Categories.SelectMany(c => c.Items).ToList();
            var images = items.Where(i => i.Kind == ResourceKind.Image).Select(i => i.Id).ToHashSet();
            var links = items.Where(i => i.Kind == ResourceKind.Link).Select(i => i.Id).ToHashSet();
            Task.Run(() =>
            {
                DownloadedSetups();
                OldExtractions();
                Thumbnails.Sweep(images);
                LinkPreviews.Sweep(links);
            });
        };
        timer.Start();
    }

    private static void DownloadedSetups()
    {
        try
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), "Kairn-Setup-*.exe"))
                try { File.Delete(f); } catch { /* encore ouvert : ce sera pour la prochaine fois */ }
        }
        catch { }
    }

    /// <summary>
    /// Un exe unique .NET extrait ses bibliothèques natives dans %TEMP%\.net\(nom)\(empreinte), un dossier par version,
    /// sans jamais les retirer. On garde celui de la version en cours, on retire les autres.
    /// </summary>
    private static void OldExtractions()
    {
        try
        {
            var current = CurrentExtraction();
            if (current is null) return; // version de développement : rien n'est extrait
            var appDir = Path.GetDirectoryName(current)!;
            var baseDir = Path.GetDirectoryName(appDir)!;
            foreach (var dir in Directory.GetDirectories(appDir))
                if (!Same(dir, current)) TryDelete(dir);
            // Les setups lancés (installation, mises à jour) extraient les leurs sous leur propre nom.
            foreach (var setup in Directory.GetDirectories(baseDir, "Kairn-Setup*"))
            {
                foreach (var dir in Directory.GetDirectories(setup)) TryDelete(dir);
                try { if (!Directory.EnumerateFileSystemEntries(setup).Any()) Directory.Delete(setup); } catch { }
            }
        }
        catch { }
    }

    /// <summary>Dossier d'où cette version a chargé ses bibliothèques natives (null si elles sont à côté de l'exe).</summary>
    private static string? CurrentExtraction()
    {
        using var self = Process.GetCurrentProcess();
        foreach (ProcessModule module in self.Modules)
        {
            if (!module.ModuleName.Equals("wpfgfx_cor3.dll", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = Path.GetDirectoryName(module.FileName);
            return dir is null || Same(dir, AppContext.BaseDirectory) ? null : dir;
        }
        return null;
    }

    /// <summary>Retire un dossier d'extraction, seulement s'il ne contient que des bibliothèques et qu'aucune ne sert.</summary>
    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.GetDirectories(dir).Length > 0) return;
            var files = Directory.GetFiles(dir);
            if (files.Any(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) return;
            // Une bibliothèque chargée par un Kairn encore ouvert ne s'ouvre pas en exclusif : on laisse alors tout le dossier.
            foreach (var f in files)
                using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
