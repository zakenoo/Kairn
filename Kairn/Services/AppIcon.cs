using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// L'icône de Kairn (trois pierres sur une tuile arrondie), redessinée aux couleurs du thème :
/// barre des tâches, zone de notification et barre de titre suivent la personnalisation.
/// </summary>
public static class AppIcon
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];
    private static string? _key;
    private static byte[]? _ico;

    /// <summary>Icône de la fenêtre (null tant que le thème n'a pas été appliqué).</summary>
    public static ImageSource? Window { get; private set; }

    /// <summary>Levé quand l'icône change (le menu de notification met la sienne à jour).</summary>
    public static event Action<byte[]>? Changed;

    public static Color StonesColor(ThemeDef t) => ThemeService.Parse(t.IconStones ?? t.Color("Accent"), "#F2F2F2");
    public static Color TileColor(ThemeDef t) => ThemeService.Parse(t.IconTile ?? t.Color("Bg"), "#000000");

    public static void Apply(ThemeDef t)
    {
        var stones = StonesColor(t);
        var tile = TileColor(t);
        var key = $"{stones}{tile}";
        if (key == _key) return;
        _key = key;
        _ico = BuildIco(stones, tile);
        var frame = BitmapFrame.Create(new MemoryStream(_ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        Window = frame;
        foreach (System.Windows.Window w in Application.Current.Windows) w.Icon = frame;
        Changed?.Invoke(_ico);
    }

    /// <summary>Icône actuelle au format .ico (pour la zone de notification).</summary>
    public static byte[]? Ico => _ico;

    /// <summary>Aperçu pour l'onglet Apparence.</summary>
    public static BitmapSource Render(int size, Color stones, Color tile)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double s = size / 256.0;
            dc.DrawRoundedRectangle(new SolidColorBrush(tile), null, new Rect(0, 0, size, size), 56 * s, 56 * s);
            // Les pierres du haut sont un peu plus fondues dans le fond, comme sur l'icône d'origine.
            dc.DrawEllipse(new SolidColorBrush(stones), null, new Point(128 * s, 184 * s), 81 * s, 26 * s);
            dc.DrawEllipse(new SolidColorBrush(Mix(stones, tile, 0.12)), null, new Point(128 * s, 132 * s), 60 * s, 21 * s);
            dc.DrawEllipse(new SolidColorBrush(Mix(stones, tile, 0.24)), null, new Point(128 * s, 87 * s), 35 * s, 17 * s);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    private static Color Mix(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

    /// <summary>Fichier .ico en mémoire, une image PNG par taille (format accepté par Windows depuis Vista).</summary>
    private static byte[] BuildIco(Color stones, Color tile)
    {
        var pngs = Sizes.Select(size =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(size, stones, tile)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }).ToList();

        using var o = new MemoryStream();
        using var w = new BinaryWriter(o);
        w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);
        int offset = 6 + 16 * Sizes.Length;
        for (int i = 0; i < Sizes.Length; i++)
        {
            byte dim = (byte)(Sizes[i] >= 256 ? 0 : Sizes[i]);
            w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        return o.ToArray();
    }
}
