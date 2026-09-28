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
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        Storage.CategoryById(value as string) is { } cat ? ThemeService.BrushFor(cat.Color) : Brushes.Transparent;
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
    public object Convert(object? value, Type t, object? p, CultureInfo c) => ThemeService.BrushFor(value as string);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
