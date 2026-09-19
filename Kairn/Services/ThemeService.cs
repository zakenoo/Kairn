using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Applique un thème en remplaçant des ressources dynamiques : couleurs, blocs (couleur ou image),
/// police, arrondis, bordures et taille de l'interface. Tout change en direct, sans redémarrer.
/// </summary>
public static class ThemeService
{
    /// <summary>Couleurs proposées pour les catégories.</summary>
    public static readonly string[] AccentPresets =
    [
        "#D6A461", "#D9785A", "#C98B8B", "#B08FC7", "#7FA7C9",
        "#6FB3B0", "#8FBF9A", "#B5B86A", "#E3C27A", "#A8A29A"
    ];

    public static string ThemesDir
    {
        get
        {
            var dir = Path.Combine(Storage.Root, "themes");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static readonly Dictionary<string, BitmapImage> ImageCache = [];

    public static void Apply(ThemeDef t)
    {
        var r = Application.Current.Resources;

        foreach (var (key, _) in ThemeTokens.ColorKeys)
            r[key + "Brush"] = Frozen(new SolidColorBrush(Parse(t.Color(key), ThemeTokens.Fallback(key))));

        var accent = Parse(t.Color("Accent"), "#D6A461");
        r["AccentColor"] = accent;
        r["AccentSoftBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(t.Dark ? (byte)0x30 : (byte)0x26, accent.R, accent.G, accent.B)));

        foreach (var (key, _, defaultColor) in ThemeTokens.ZoneKeys)
            r["Zone" + key + "Brush"] = ZoneBrush(t, t.Zone(key), defaultColor);

        try { r["AppFont"] = new FontFamily(t.Font); } catch { r["AppFont"] = new FontFamily("Segoe UI"); }
        r["Radius"] = new CornerRadius(t.Radius);
        r["RadiusSmall"] = new CornerRadius(Math.Max(0, t.Radius * 0.6));
        r["RadiusPill"] = new CornerRadius(999);
        r["CardBorder"] = new Thickness(t.BorderWidth);
        var scale = new ScaleTransform(t.UiScale, t.UiScale);
        scale.Freeze();
        r["UiScale"] = scale;
        r["LogoBrush"] = Frozen(new SolidColorBrush(AppIcon.StonesColor(t)));
        AppIcon.Apply(t);

        foreach (Window w in Application.Current.Windows) ApplyWindowChrome(w, t.Dark);
    }

    /// <summary>
    /// Pinceau d'un bloc : sa couleur, ou son image recouverte d'un voile de la couleur du bloc
    /// (pour garder le texte lisible quelle que soit l'image).
    /// </summary>
    private static Brush ZoneBrush(ThemeDef t, ZoneStyle z, string defaultColorKey)
    {
        var color = Parse(z.Color ?? t.Color(defaultColorKey), "#000000");
        var image = LoadImage(z.Image);

        Brush brush;
        if (image is null)
        {
            brush = new SolidColorBrush(color);
        }
        else
        {
            var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            var group = new DrawingGroup();
            group.Children.Add(new ImageDrawing(image, rect));
            var veil = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(z.Veil, 0, 1) * 255), color.R, color.G, color.B));
            group.Children.Add(new GeometryDrawing(veil, null, new RectangleGeometry(rect)));
            brush = new DrawingBrush(group) { Stretch = Stretch.UniformToFill };
        }
        brush.Opacity = Math.Clamp(z.Opacity, 0, 1);
        brush.Freeze();
        return brush;
    }

    private static BitmapImage? LoadImage(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        var path = Path.IsPathRooted(file) ? file : Path.Combine(ThemesDir, file);
        if (ImageCache.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.DecodePixelWidth = 1920; // assez pour un écran, sans garder une photo 6000 px en mémoire
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return ImageCache[path] = img;
        }
        catch { return null; }
    }

    /// <summary>Copie une image dans le dossier des thèmes et renvoie son nom de fichier.</summary>
    public static string ImportImage(string sourcePath)
    {
        var name = Path.GetFileName(sourcePath);
        var dest = Path.Combine(ThemesDir, name);
        for (int i = 2; File.Exists(dest) && !SameFile(dest, sourcePath); i++)
            dest = Path.Combine(ThemesDir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        if (!File.Exists(dest)) File.Copy(sourcePath, dest);
        return Path.GetFileName(dest);
    }

    private static bool SameFile(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    // ===================== Partage de thèmes (.kairntheme = zip : theme.json + images) =====================

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Export(ThemeDef t, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("theme.json");
        using (var s = entry.Open()) JsonSerializer.Serialize(s, t, Json);
        foreach (var img in t.Zones.Values.Select(z => z.Image).Where(i => !string.IsNullOrEmpty(i)).Distinct())
        {
            var file = Path.Combine(ThemesDir, img!);
            if (File.Exists(file)) zip.CreateEntryFromFile(file, "images/" + img);
        }
    }

    public static ThemeDef? Import(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("theme.json");
        if (entry is null) return null;
        ThemeDef? t;
        using (var s = entry.Open()) t = JsonSerializer.Deserialize<ThemeDef>(s, Json);
        if (t is null) return null;
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("images/") && e.Name.Length > 0))
        {
            // Seul le nom de fichier est gardé : une archive ne peut rien écrire hors du dossier des thèmes.
            var dest = Path.Combine(ThemesDir, Path.GetFileName(e.Name));
            if (!File.Exists(dest)) e.ExtractToFile(dest);
        }
        return t;
    }

    // ===================== Utilitaires =====================

    public static Color Parse(string? hex, string fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex ?? fallback); }
        catch { return (Color)ColorConverter.ConvertFromString(fallback); }
    }

    public static string ToHex(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Coins arrondis et barre de titre sombre natifs de Windows 11.</summary>
    public static void ApplyWindowChrome(Window w, bool dark)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));    // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
    }
}
