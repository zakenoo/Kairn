using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Kairn.Services;

/// <summary>
/// Détecte les programmes installés (liste « Applications » de Windows, lue en local dans le registre)
/// et récupère leur icône. Rien ne sort du PC.
/// </summary>
public static class InstalledApps
{
    private record Entry(string Name, string? Icon, string? Location);

    private static List<Entry>? _entries;
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Relit la liste (appelé à l'ouverture de l'onglet Garde).</summary>
    public static void Reset() => _entries = null;

    private static List<Entry> Entries => _entries ??= Scan();

    private static List<Entry> Scan()
    {
        var list = new List<Entry>();
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
                if (root is null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is not string name || name.Length == 0) continue;
                    list.Add(new Entry(name, CleanIconPath(k.GetValue("DisplayIcon") as string), k.GetValue("InstallLocation") as string));
                }
            }
            catch { /* clé inaccessible : on passe */ }
        }
        return list;
    }

    private static string? CleanIconPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var p = raw.Trim().Trim('"');
        int comma = p.LastIndexOf(',');
        if (comma > 2 && int.TryParse(p[(comma + 1)..].Trim(), out _)) p = p[..comma].Trim('"', ' ');
        return File.Exists(p) ? p : null;
    }

    /// <summary>L'app est-elle sur ce PC ? Renvoie aussi un fichier d'où tirer son icône.</summary>
    public static (bool Installed, string? IconPath) Find(CatalogApp app, IReadOnlyDictionary<string, uint> running)
    {
        foreach (var proc in app.Processes)
            if (running.TryGetValue(proc, out var pid)) return (true, ProcessPath(pid));

        var match = Entries.FirstOrDefault(e => app.InstallNames.Any(n => e.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)));
        if (match is null) return (false, null);
        if (match.Icon != null) return (true, match.Icon);
        if (match.Location is { Length: > 0 } loc && Directory.Exists(loc))
        {
            try
            {
                // L'exe directement dans le dossier, ou un niveau plus bas (ex : Discord\app-1.0.9\Discord.exe).
                var dirs = new[] { loc }.Concat(Directory.GetDirectories(loc).OrderByDescending(d => d));
                foreach (var dir in dirs)
                    foreach (var proc in app.Processes)
                    {
                        var exe = Path.Combine(dir, proc + ".exe");
                        if (File.Exists(exe)) return (true, exe);
                    }
                if (Directory.GetFiles(loc, "*.ico").FirstOrDefault() is { } ico) return (true, ico);
            }
            catch { }
        }
        return (true, null);
    }

    /// <summary>Instantané des processus en cours : nom → pid (une seule énumération du système).</summary>
    public static Dictionary<string, uint> RunningSnapshot()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
            using (p) map.TryAdd(p.ProcessName, (uint)p.Id);
        return map;
    }

    /// <summary>Chemin de l'exe d'un processus en cours, s'il tourne.</summary>
    public static string? RunningPath(string processName)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
                if (ProcessPath((uint)p.Id) is { } path) return path;
        }
        return null;
    }

    /// <summary>Apps ouvertes avec une fenêtre visible : (nom lisible, nom de processus, chemin de l'exe).</summary>
    public static List<(string Name, string Process, string? Path)> OpenWindows()
    {
        var self = Environment.ProcessId;
        var result = new List<(string, string, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "Kairn" };
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || p.MainWindowTitle.Length == 0 || !seen.Add(p.ProcessName)) continue;
                    var path = ProcessPath((uint)p.Id);
                    result.Add((FriendlyName(path) ?? p.ProcessName, p.ProcessName, path));
                }
                catch { }
            }
        }
        return result.OrderBy(r => r.Item1).ToList();
    }

    /// <summary>Nom lisible d'un exe (« Discord » plutôt que « Discord.exe »), tiré de ses propriétés.</summary>
    public static string? FriendlyName(string? exePath)
    {
        if (exePath is null || !File.Exists(exePath)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
            return string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(exePath) : name.Trim();
        }
        catch { return Path.GetFileNameWithoutExtension(exePath); }
    }

    /// <summary>Icône d'un exe ou d'un .ico, prête pour WPF (mise en cache).</summary>
    public static ImageSource? Icon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            using var icon = path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                ? new System.Drawing.Icon(path, 48, 48)
                : System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                img = src;
            }
        }
        catch { }
        return IconCache[path] = img;
    }

    // ---- Chemin d'un processus sans énumérer tout le système ----

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, System.Text.StringBuilder name, ref uint size);

    public static string? ProcessPath(uint pid)
    {
        const uint QueryLimitedInformation = 0x1000;
        var h = OpenProcess(QueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
