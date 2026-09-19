using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kairn.Services;

/// <summary>Ouvre des outils, des sites ou des fichiers, et retrouve leur icône.</summary>
public static class Launcher
{
    public static bool IsUrl(string s) =>
        Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>Complète « canva.com » en « https://canva.com ».</summary>
    public static string Normalize(string target)
    {
        var t = target.Trim().Trim('"');
        if (IsUrl(t) || File.Exists(t) || Directory.Exists(t)) return t;
        if (t.Contains('.') && !t.Contains(' ') && !t.Contains('\\') && IsUrl("https://" + t)) return "https://" + t;
        return t;
    }

    public static bool Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>Nom lisible pour une cible : « YouTube », « Canva », « Photoshop »…</summary>
    public static string NameFor(string target)
    {
        if (IsUrl(target))
        {
            var host = new Uri(target).Host.Replace("www.", "");
            if (host.Contains("youtu")) return L.T("link.youtube");
            if (host.Contains("pinterest")) return "Pinterest";
            var main = host.Split('.').Reverse().Skip(1).FirstOrDefault() ?? host;
            return char.ToUpper(main[0]) + main[1..];
        }
        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(target);
        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return InstalledApps.FriendlyName(target) ?? Path.GetFileNameWithoutExtension(target);
        return Path.GetFileName(target);
    }

    // ===================== Icônes =====================

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    /// <summary>Icône Windows d'un programme, raccourci ou fichier (null pour un site web).</summary>
    public static ImageSource? Icon(string target)
    {
        if (IsUrl(target) || !(File.Exists(target) || Directory.Exists(target))) return null;
        if (Cache.TryGetValue(target, out var cached)) return cached;
        ImageSource? img = null;
        var info = new SHFILEINFO();
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;
        if (SHGetFileInfo(target, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                img = src;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        return Cache[target] = img;
    }

    /// <summary>Glyphe (Segoe Fluent Icons) pour une cible sans icône propre.</summary>
    public static string Glyph(string target) =>
        IsUrl(target) ? (target.Contains("youtu") ? "" : "") : "";

    // ===================== Programmes du menu Démarrer =====================

    private static List<(string Name, string Path)>? _startMenu;

    /// <summary>Raccourcis du menu Démarrer (tous les programmes installés « classiques »), sans doublons ni désinstalleurs.</summary>
    public static List<(string Name, string Path)> StartMenuApps()
    {
        if (_startMenu != null) return _startMenu;
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };
        var skip = new[] { "uninstall", "désinstaller", "desinstaller", "readme", "lisez-moi", "help", "aide", "website", "site web", "documentation", "release notes" };
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).ToList(); } catch { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                result.TryAdd(name, f);
            }
        }
        return _startMenu = result.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key).ToList();
    }
}
