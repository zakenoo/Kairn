using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// Pose la miniature d'une image de la bibliothèque sur sa carte sans figer l'interface :
/// l'image est lue (ou fabriquée la première fois) en arrière-plan, puis affichée.
/// En XAML : l:ThumbnailLoader.Item="{Binding}" sur la bordure qui porte la miniature.
/// </summary>
public static class ThumbnailLoader
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(ResourceItem), typeof(ThumbnailLoader), new PropertyMetadata(null, OnItemChanged));

    public static ResourceItem? GetItem(DependencyObject d) => (ResourceItem?)d.GetValue(ItemProperty);
    public static void SetItem(DependencyObject d, ResourceItem? value) => d.SetValue(ItemProperty, value);

    private static async void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        border.Background = null;
        if (e.NewValue is not ResourceItem { Kind: ResourceKind.Image } item) return;
        var id = item.Id;
        var path = Storage.AbsoluteLibraryPath(item.Content);
        var img = Thumbnails.Cached(id, path) ?? await Task.Run(() => Thumbnails.Get(id, path));
        // Pendant le chargement, la carte a pu changer d'élément ou disparaître.
        if (img is null || !ReferenceEquals(GetItem(border), item)) return;
        var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        border.Background = brush;
    }
}
