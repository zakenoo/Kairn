using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// Pose la miniature d'une image de la bibliothèque (ou l'aperçu d'un lien, voir LinkPreviews) sur sa carte sans figer l'interface :
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
        // Un lien n'a de miniature que si sa page propose une image : la place reste fermée en attendant.
        bool link = e.NewValue is ResourceItem { Kind: ResourceKind.Link };
        if (link) border.Visibility = Visibility.Collapsed;
        else border.ClearValue(UIElement.VisibilityProperty);

        if (e.NewValue is not ResourceItem { Kind: ResourceKind.Image or ResourceKind.Link } item) return;
        var id = item.Id;
        BitmapSource? img;
        if (link)
        {
            var preview = await LinkPreviews.GetAsync(id, item.Content);
            img = preview.Image;
            // Le vrai titre de la page remplace « Épingle Pinterest » ou « site · chemin ».
            // Un titre que la personne a déjà changé elle-même n'est jamais touché.
            if (preview.Title is { } title && item.Title == LibraryView.LinkTitle(item.Content) && title != item.Title)
            {
                item.Title = title;
                Storage.Save();
            }
        }
        else
        {
            var path = Storage.AbsoluteLibraryPath(item.Content);
            img = Thumbnails.Cached(id, path) ?? await Task.Run(() => Thumbnails.Get(id, path));
        }
        // Pendant le chargement, la carte a pu changer d'élément ou disparaître.
        if (img is null || !ReferenceEquals(GetItem(border), item)) return;
        var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        border.Background = brush;
        if (link) border.Visibility = Visibility.Visible;
    }
}
