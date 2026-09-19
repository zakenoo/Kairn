using System.IO;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Fichiers de la bibliothèque rangés comme les catégories : library\Dessin\Anatomie\image.png.
/// Renommer, déplacer ou supprimer une catégorie remet les dossiers d'aplomb (Sync).
/// </summary>
public static class LibraryFiles
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>Nom de dossier sûr pour Windows à partir du nom de la catégorie.</summary>
    private static string Safe(Category c)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(c.Name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim().TrimEnd('.', ' ');
        if (name.Length > 60) name = name[..60].TrimEnd('.', ' ');
        if (name.Length == 0) name = "Catégorie " + c.Id[..6];
        return Reserved.Contains(name) ? "_" + name : name;
    }

    /// <summary>Dossier (relatif à library) d'une catégorie : ses parents, puis elle. Deux sœurs de même nom : « Dessin (2) ».</summary>
    public static string FolderFor(Category c)
    {
        var parts = new List<string>();
        var seen = new HashSet<Category>();
        for (var cur = c; cur != null && seen.Add(cur); cur = Storage.ParentOf(cur))
        {
            var parent = Storage.ParentOf(cur);
            var name = Safe(cur);
            var twins = Storage.ChildrenOf(parent).Where(s => Safe(s).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            int rank = twins.IndexOf(cur);
            parts.Insert(0, rank > 0 ? $"{name} ({rank + 1})" : name);
        }
        return Path.Combine([.. parts]);
    }

    /// <summary>Copie un fichier dans le dossier de la catégorie et renvoie son chemin relatif.</summary>
    public static string Import(Category cat, string sourcePath)
    {
        var dest = FreePath(Path.Combine(Storage.LibraryDir, FolderFor(cat)), Path.GetFileName(sourcePath));
        File.Copy(sourcePath, dest);
        return Path.GetRelativePath(Storage.LibraryDir, dest);
    }

    /// <summary>Range le fichier d'un élément dans le dossier de sa catégorie (s'il n'y est pas déjà).</summary>
    public static string Place(string relative, Category cat)
    {
        var src = Storage.AbsoluteLibraryPath(relative);
        var dir = Path.Combine(Storage.LibraryDir, FolderFor(cat));
        if (!File.Exists(src) || string.Equals(Path.GetDirectoryName(Path.GetFullPath(src)), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
            return relative;
        var dest = FreePath(dir, Path.GetFileName(src));
        File.Move(src, dest);
        return Path.GetRelativePath(Storage.LibraryDir, dest);
    }

    private static string FreePath(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, name);
        for (int i = 2; File.Exists(dest); i++)
            dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return dest;
    }

    /// <summary>
    /// Met chaque fichier dans le dossier qui porte le nom de sa catégorie, puis retire les dossiers devenus vides
    /// (dont les anciens dossiers nommés par identifiant). Un fichier ouvert ailleurs reste où il est, sans rien casser.
    /// </summary>
    public static void Sync()
    {
        if (!Directory.Exists(Storage.LibraryDir)) return;
        bool changed = false;
        foreach (var cat in Storage.Data.Categories)
            foreach (var item in cat.Items.Where(i => i.Kind is ResourceKind.Image or ResourceKind.File))
            {
                try
                {
                    var rel = Place(item.Content, cat);
                    if (rel != item.Content) { item.Content = rel; changed = true; }
                }
                catch { /* fichier verrouillé : on réessaiera au prochain démarrage */ }
            }
        if (changed) Storage.Save();
        RemoveEmptyFolders(Storage.LibraryDir, isRoot: true);
    }

    private static bool RemoveEmptyFolders(string dir, bool isRoot)
    {
        bool empty = true;
        foreach (var sub in Directory.GetDirectories(dir))
            if (!RemoveEmptyFolders(sub, false)) empty = false;
        if (Directory.EnumerateFiles(dir).Any()) empty = false;
        if (empty && !isRoot)
            try { Directory.Delete(dir); } catch { return false; }
        return empty;
    }
}
