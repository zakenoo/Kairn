using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Surveille les apps de distraction (Discord par défaut) pendant les blocs de travail.
/// Ne tourne qu'avec un timer léger (toutes les 2 s) et ne lit que le nom du processus au premier plan.
/// </summary>
public sealed class FocusGuard
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private string? _lastBlockId;
    private DateTime _lastNudge = DateTime.MinValue;

    /// <summary>Levé quand il faut afficher le rappel doux (tâche en cours, nom de l'app).</summary>
    public event Action<PlanTask, string>? Nudge;
    /// <summary>Levé quand une app a été fermée.</summary>
    public event Action<PlanTask, string>? Closed;
    /// <summary>Levé quand une tâche rythmée passe du travail à la pause, ou de la pause au travail.</summary>
    public event Action<PlanTask, Rhythm.Segment, int>? RhythmChanged;

    /// <summary>Pause temporaire du garde (ex : « j'ai besoin de Discord 15 min »).</summary>
    public DateTime SnoozedUntil { get; set; } = DateTime.MinValue;

    public FocusGuard() => _timer.Tick += (_, _) => Tick();

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public static PlanTask? CurrentTask()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var yesterday = today.AddDays(-1);
        var t = now.TimeOfDay;
        return Storage.Data.Tasks.Where(x => !x.Floating).FirstOrDefault(x =>
            (x.Date == today && x.Start <= t && (x.End > t || x.End <= x.Start)) ||
            (x.Date == yesterday && x.End <= x.Start && t < x.End));
    }

    private void Tick()
    {
        var s = Storage.Settings;
        AutoOpenLinks();
        WatchRhythm();
        if (s.FocusMode == FocusMode.Off || (s.BlockedApps.Count == 0 && !s.WatchFullscreen) || DateTime.Now < SnoozedUntil) return;

        var task = CurrentTask();
        var phase = task is null ? null : Rhythm.Current(task, DateTime.Now);
        // Pause d'une tâche rythmée : comme une vraie pause, le garde se repose aussi.
        if (task is null || task.IsBreak || task.Done || phase is { IsPause: true })
        {
            _lastBlockId = null;
            return;
        }

        // Chaque session compte comme un nouveau bloc : le mode « fermeture au début » refait son ménage après la pause.
        var blockId = task.Id + "#" + (phase?.Session ?? 0);
        bool blockStarted = _lastBlockId != blockId;
        _lastBlockId = blockId;

        switch (s.FocusMode)
        {
            case FocusMode.Gentle:
                var fg = ForegroundProcessName();
                if (fg != null && IsBlocked(fg)) TryNudge(task, DisplayName(fg));
                break;

            case FocusMode.CloseAtStart:
                if (blockStarted) CloseBlocked(task);
                break;

            case FocusMode.Strict:
                CloseBlocked(task);
                break;
        }

        // Plein écran (jeu lancé depuis un launcher, vidéo…) : toujours un simple rappel, jamais de fermeture,
        // car on ne sait pas ce que c'est.
        if (s.WatchFullscreen && ForegroundIsFullscreen(out var name)) TryNudge(task, name);
    }

    private string? _autoOpenedId;

    /// <summary>
    /// Tâche marquée « ouvrir automatiquement » : ses liens s'ouvrent une seule fois, dans les 2 premières minutes du bloc
    /// (pas si on allume le PC en plein milieu d'un bloc déjà bien entamé).
    /// </summary>
    private void AutoOpenLinks()
    {
        var task = CurrentTask();
        if (task is null || task.Id == _autoOpenedId) return;
        _autoOpenedId = task.Id;
        if (!task.AutoOpen || task.Done || task.Links.Count == 0) return;
        var elapsed = DateTime.Now.TimeOfDay - task.Start;
        if (elapsed < TimeSpan.Zero) elapsed += TimeSpan.FromDays(1);
        if (elapsed > TimeSpan.FromMinutes(2)) return;
        foreach (var l in task.Links) Launcher.Open(l.Target);
    }

    private string? _lastPhaseKey;

    /// <summary>
    /// Annonce les changements de phase d'une tâche rythmée (« pause ! », « on reprend »).
    /// Seulement si le changement vient d'avoir lieu : allumer le PC en pleine pause ne déclenche rien.
    /// </summary>
    private void WatchRhythm()
    {
        var task = CurrentTask();
        var now = DateTime.Now;
        var seg = task is null || task.Done ? null : Rhythm.Current(task, now);
        var key = seg is null ? null : $"{task!.Id}#{seg.Session}#{seg.IsPause}";
        if (key == _lastPhaseKey) return;
        bool wasSameTask = _lastPhaseKey != null && task != null && _lastPhaseKey.StartsWith(task.Id + "#");
        _lastPhaseKey = key;
        if (seg is null || !wasSameTask || seg.From == TimeSpan.Zero) return;
        if (Rhythm.Elapsed(task!, now) - seg.From > TimeSpan.FromMinutes(1)) return;
        RhythmChanged?.Invoke(task!, seg, Rhythm.Sessions(Rhythm.Segments(task!)));
    }

    private void TryNudge(PlanTask task, string what)
    {
        if (DateTime.Now - _lastNudge < TimeSpan.FromSeconds(90)) return;
        _lastNudge = DateTime.Now;
        Nudge?.Invoke(task, what);
    }

    private bool IsBlocked(string processName) =>
        Storage.Settings.BlockedApps.Any(a => string.Equals(Normalize(a), processName, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string app) =>
        app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;

    /// <summary>« Steam » plutôt que « steam », « Riot Client » plutôt que « RiotClientServices ».</summary>
    public static string DisplayName(string process) =>
        AppCatalog.ForProcess(process)?.Name
        ?? Storage.Settings.CustomApps.FirstOrDefault(c => c.Process.Equals(process, StringComparison.OrdinalIgnoreCase))?.Name
        ?? process;

    private void CloseBlocked(PlanTask task)
    {
        var closed = new HashSet<string>();
        foreach (var app in Storage.Settings.BlockedApps.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var procs = Process.GetProcessesByName(app);
            if (procs.Length == 0) continue;
            foreach (var p in procs)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.Dispose();
            }
            closed.Add(DisplayName(app));
        }
        foreach (var name in closed) Closed?.Invoke(task, name);
    }

    // ---- Détection du plein écran ----

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);

    private static readonly HashSet<string> NeverFullscreenAlert = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "Kairn", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "LockApp" };

    private bool ForegroundIsFullscreen(out string name)
    {
        name = "";
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(hwnd, cls, 64);
        if (cls.ToString() is "Progman" or "WorkerW") return false; // bureau

        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return false;
        var m = info.Monitor;
        if (r.L > m.L || r.T > m.T || r.R < m.R || r.B < m.B) return false;

        var process = ForegroundProcessName();
        if (process is null || NeverFullscreenAlert.Contains(process)) return false;
        name = DisplayName(process);
        return true;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, System.Text.StringBuilder name, ref uint size);

    private uint _lastPid;
    private string? _lastName;

    /// <summary>
    /// Nom du processus au premier plan. On interroge directement ce seul processus
    /// (Process.GetProcessById énumère tous les processus du système à chaque appel, trop coûteux toutes les 2 s).
    /// </summary>
    private string? ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _lastPid) return _lastName;

        const uint QueryLimitedInformation = 0x1000;
        var h = OpenProcess(QueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
            _lastPid = pid;
            _lastName = System.IO.Path.GetFileNameWithoutExtension(sb.ToString());
            return _lastName;
        }
        finally { CloseHandle(h); }
    }
}
