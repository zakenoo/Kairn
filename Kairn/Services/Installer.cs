using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Kairn.Services;

/// <summary>
/// Installation, mise à jour et désinstallation, sans droits administrateur.
/// Le setup est Kairn lui-même : le même fichier, nommé « Kairn-Setup-x.y.z.exe », s'ouvre en mode installation
/// et se copie dans %LocalAppData%\Programs\Kairn.
/// </summary>
public static class Installer
{
    /// <summary>Test : « KAIRN_SETUP_SANDBOX=dossier » installe dans ce dossier, avec des clés de registre à part, sans rien fermer.</summary>
    private static readonly string? Sandbox = Environment.GetEnvironmentVariable("KAIRN_SETUP_SANDBOX") is { Length: > 0 } s ? s : null;
    private static string UninstallKey => Sandbox is null ? @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Kairn" : @"Software\KairnSandbox\Uninstall";
    private static string RunKey => Sandbox is null ? @"Software\Microsoft\Windows\CurrentVersion\Run" : @"Software\KairnSandbox\Run";
    public const string UninstallArg = "--uninstall";
    public const string UpdateArg = "--update";

    public static string InstallDir => Path.Combine(Sandbox ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Kairn");
    public static string InstalledExe => Path.Combine(InstallDir, "Kairn.exe");
    private static string StartMenuLink => Path.Combine(Sandbox ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Kairn.lnk");
    private static string DesktopLink => Path.Combine(Sandbox ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Sandbox is null ? "Kairn.lnk" : "Desktop-Kairn.lnk");

    public static Version CurrentVersion => typeof(Installer).Assembly.GetName().Version ?? new Version(0, 0);
    public static string VersionText(Version v) => v.Build >= 0 ? v.ToString(3) : v.ToString(2);

    /// <summary>Ce fichier est-il un setup ? (nom « Kairn-Setup… » ou argument --setup)</summary>
    public static bool IsSetupMode(string[] args) =>
        args.Contains("--setup") ||
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static Version? InstalledVersion
    {
        get
        {
            try { return IsInstalled && Version.TryParse(FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion, out var v) ? v : null; }
            catch { return null; }
        }
    }

    public record Options(bool StartWithWindows, bool DesktopShortcut);

    /// <summary>
    /// Copie Kairn à sa place, crée les raccourcis et l'entrée « Applications » de Windows.
    /// <paramref name="target"/> : fichier à remplacer (par défaut l'emplacement d'installation).
    /// </summary>
    public static async Task InstallAsync(Options? options, IProgress<(string Step, double Fraction)> progress, string? target = null)
    {
        target ??= InstalledExe;
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("process path");

        progress.Report(("close", 0.02));
        CloseOtherInstances(self);

        progress.Report(("copy", 0.05));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".new";
        await using (var src = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buf = new byte[1 << 20];
            long done = 0, total = src.Length;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                progress.Report(("copy", 0.05 + 0.75 * done / total));
            }
        }
        // Remplacement : si l'ancien fichier est encore verrouillé un instant, on réessaie.
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, target, overwrite: true); break; }
            catch (IOException) when (i < 20) { await Task.Delay(250); }
        }

        progress.Report(("shortcuts", 0.85));
        if (string.Equals(target, InstalledExe, StringComparison.OrdinalIgnoreCase))
        {
            CreateShortcut(StartMenuLink, target);
            if (options?.DesktopShortcut == true) CreateShortcut(DesktopLink, target);
            RegisterUninstall(target);
        }
        if (options != null) SetStartup(target, options.StartWithWindows);
        else RetargetStartup(target);
        progress.Report(("done", 1));
    }

    /// <summary>Ferme les autres Kairn (l'app se sauvegarde à chaque modification : rien n'est perdu).</summary>
    private static void CloseOtherInstances(string self)
    {
        if (Sandbox != null) return;
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == me || !p.ProcessName.StartsWith("Kairn", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.ProcessName.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    private static void CreateShortcut(string link, string exe)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = exe;
            sc.WorkingDirectory = Path.GetDirectoryName(exe);
            sc.IconLocation = exe + ",0";
            sc.Description = "Kairn";
            sc.Save();
        }
        catch { /* un raccourci manquant n'empêche pas l'installation */ }
    }

    private static void RegisterUninstall(string exe)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            k.SetValue("DisplayName", "Kairn");
            k.SetValue("DisplayVersion", VersionText(CurrentVersion));
            k.SetValue("Publisher", "Zak");
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("InstallLocation", Path.GetDirectoryName(exe)!);
            k.SetValue("UninstallString", $"\"{exe}\" {UninstallArg}");
            k.SetValue("URLInfoAbout", "https://github.com/zakenoo/Kairn");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
        catch { }
    }


    private static void SetStartup(string exe, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key?.SetValue("Kairn", $"\"{exe}\" {StartupService.StartupArg}");
            else key?.DeleteValue("Kairn", throwOnMissingValue: false);
        }
        catch { }
    }

    /// <summary>Mise à jour : si Kairn démarrait avec Windows, il continue (en pointant vers le bon fichier).</summary>
    private static void RetargetStartup(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key?.GetValue("Kairn") is string) key.SetValue("Kairn", $"\"{exe}\" {StartupService.StartupArg}");
        }
        catch { }
    }

    /// <summary>Retire Kairn : raccourcis, entrée Windows, démarrage auto, puis le dossier (après la fermeture de ce processus).</summary>
    public static void Uninstall(bool deleteData)
    {
        CloseOtherInstances(Environment.ProcessPath ?? "");
        foreach (var link in new[] { StartMenuLink, DesktopLink })
            try { if (File.Exists(link)) File.Delete(link); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
        SetStartup("", enabled: false);
        if (deleteData) try { Directory.Delete(Storage.Root, recursive: true); } catch { }

        // Un exe ne peut pas s'effacer lui-même : une petite commande le fait juste après notre fermeture.
        var dir = InstallDir;
        if (Directory.Exists(dir) && (Environment.ProcessPath ?? "").StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        else
            try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
