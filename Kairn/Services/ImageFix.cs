using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kairn.Services;

/// <summary>
/// Les captures d'écran collées depuis le presse-papier de Windows arrivent souvent avec un canal alpha à zéro :
/// les couleurs sont là, mais chaque pixel est « invisible ». On retire cette transparence fantôme.
/// </summary>
public static class ImageFix
{
    /// <summary>Image du presse-papier prête à enregistrer : le PNG d'origine s'il existe, sinon l'image rendue opaque.</summary>
    public static BitmapSource? FromClipboard()
    {
        try
        {
            if (Clipboard.GetData("PNG") is MemoryStream png)
            {
                var decoded = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return IsFullyTransparent(decoded) ? Opaque(decoded) : decoded;
            }
        }
        catch { /* PNG illisible : on se rabat sur l'image classique */ }
        var img = Clipboard.GetImage();
        return img is null ? null : Opaque(img);
    }

    /// <summary>Même image sans canal alpha.</summary>
    public static BitmapSource Opaque(BitmapSource src)
    {
        var b = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
        b.Freeze();
        return b;
    }

    /// <summary>Vrai si l'image a un canal alpha et qu'il vaut zéro partout.</summary>
    public static bool IsFullyTransparent(BitmapSource src)
    {
        // Le défaut du presse-papier ne produit que ces formats 32 bits.
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32) return false;
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var row = new byte[stride];
        // Une soixantaine de lignes suffisent pour le savoir, même sur une grande image.
        for (int y = 0; y < h; y += Math.Max(1, h / 64))
        {
            src.CopyPixels(new Int32Rect(0, y, w, 1), row, stride, 0);
            for (int i = 3; i < stride; i += 4)
                if (row[i] != 0) return false;
        }
        return true;
    }

    /// <summary>Répare sur le disque une image de la bibliothèque enregistrée avec la transparence fantôme.</summary>
    public static bool RepairFile(string path)
    {
        try
        {
            BitmapSource full;
            using (var fs = File.OpenRead(path))
                full = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (!IsFullyTransparent(full)) return false;
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(Opaque(full)));
                enc.Save(fs);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }
}
