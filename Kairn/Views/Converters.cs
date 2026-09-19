using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>Id de catégorie → pinceau de sa couleur (transparent si aucune).</summary>
public class CategoryBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var cat = Storage.CategoryById(value as string);
        if (cat is null) return Brushes.Transparent;
        var b = new SolidColorBrush(ThemeService.Parse(cat.Color, "#F2F2F2"));
        b.Freeze();
        return b;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Id de catégorie → « 🎨 Dessin » (ou « 📂 Dessin › Anatomie »).</summary>
public class CategoryNameConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        Storage.CategoryById(value as string) is { } cat ? $"{cat.Emoji} {Storage.CategoryPath(cat)}" : "";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public class ResourceIconConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        ResourceKind.Link => "",
        ResourceKind.Note => "",
        ResourceKind.Image => "",
        _ => ""
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Chemin relatif d'une image de la bibliothèque → miniature (décodée petite, fichier non verrouillé).</summary>
public class ThumbConverter : IValueConverter
{
    private static readonly HashSet<string> Checked = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not ResourceItem { Kind: ResourceKind.Image, Content: var rel }) return null;
        try
        {
            var path = Storage.AbsoluteLibraryPath(rel);
            // Capture collée avant la correction : on la répare une fois pour toutes.
            if (Checked.Add(path) && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ImageFix.RepairFile(path);
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            img.DecodePixelWidth = 320;
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>URL → « youtube.com », chemin → nom de fichier.</summary>
public class SubtitleConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        ResourceItem { Kind: ResourceKind.Link } r when Uri.TryCreate(r.Content, UriKind.Absolute, out var u) => u.Host.Replace("www.", ""),
        ResourceItem { Kind: ResourceKind.Note } r => r.Content.Length > 180 ? r.Content[..180] + "…" : r.Content,
        ResourceItem r => System.IO.Path.GetFileName(r.Content),
        _ => ""
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var b = new SolidColorBrush(ThemeService.Parse(value as string, "#F2F2F2"));
        b.Freeze();
        return b;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
