using System.Threading;
using System.Text.Json;
using System.Windows;
using Kairn.Services;
using Kairn.Services.Assistant;
using Kairn.Views;
using Forms = System.Windows.Forms;

namespace Kairn;

public partial class App : Application
{
    // Une instance par dossier de données : une copie portable (dossier « data » à côté de l'exe) vit sa vie à part.
    private static readonly string InstanceName = "Kairn.SingleInstance." +
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(Storage.Root.ToLowerInvariant())))[..12];
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private Forms.NotifyIcon? _tray;

    public FocusGuard Guard { get; } = new();
    public static new App Current => (App)Application.Current;
    public MainWindow? MainWin { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Setup, mise à jour ou désinstallation : le même exe, sans l'app elle-même.
        if (Installer.IsSetupMode(e.Args) || e.Args.Contains(Installer.UninstallArg) || e.Args.Contains(Installer.UpdateArg))
        {
            base.OnStartup(e);
            RunSetup(e.Args);
            return;
        }

        // Une seule instance : si l'app tourne déjà, on lui demande juste de s'afficher.
        _mutex = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(InstanceName + ".show").Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".show");
        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.Invoke(ShowMain);
        }) { IsBackground = true }.Start();

        base.OnStartup(e);
        // Ancienne version renommée pendant une mise à jour (elle tournait encore) : on fait le ménage.
        foreach (var old in System.IO.Directory.GetFiles(AppContext.BaseDirectory, "Kairn.old*.exe"))
            try { System.IO.File.Delete(old); } catch { /* encore ouverte : ce sera pour la prochaine fois */ }
        // Une erreur imprévue dans l'interface est notée dans errors.log au lieu de fermer l'app (et le garde avec).
        DispatcherUnhandledException += (_, ex) =>
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(Storage.Root, "errors.log"), $"[{DateTime.Now:s}] {ex.Exception}\n\n"); } catch { }
            ex.Handled = true;
        };
        Storage.Load();
        LibraryFiles.Sync(); // dossiers de la bibliothèque au nom des catégories (et anciens dossiers à identifiant convertis)
        Loc.Instance.Load(Storage.Settings.Language ?? Loc.SystemDefault());
        CarryOver.RollOver();
        ThemeService.Apply(Storage.Settings.Theme);
        StartupService.Apply(Storage.Settings.StartWithWindows);

        // Outils de test sans interface : « --assistant-install » et « --assistant-demo demande.json résultat.json ».
        int dev = Array.FindIndex(e.Args, a => a.StartsWith("--assistant-"));
        if (dev >= 0) { AssistantDevTools.Run(e.Args[dev..]); return; }

        CreateTray();

        Guard.Nudge += (task, app) => NudgeWindow.Show(task, app, closed: false);
        Guard.Closed += (task, app) => NudgeWindow.Show(task, app, closed: true);
        Guard.RhythmChanged += NudgeWindow.ShowRhythm;
        Guard.Start();

        // « Ça commence dans un quart d'heure » : le rappel que rien ne remplace quand on ne sent pas le temps.
        Reminders.Due += NudgeWindow.ShowReminder;
        Reminders.Start();
        // Ctrl+Alt+K depuis n'importe où : noter une idée avant qu'elle s'échappe.
        HotKey.Apply(QuickAddWindow.Open);

        // Calendrier iCloud, seulement s'il a été relié : les rendez-vous descendent, les séances remontent.
        CalendarSync.Changed += () => Dispatcher.BeginInvoke(Refreshed);
        CalendarSync.Start();

        if (Storage.Settings.CheckUpdates) Updater.StartAutoCheck();

        MainWin = new MainWindow();
        MainWin.Show();

        // « Kairn.exe --snapshot dossier [langue] » : enregistre une image de chaque page puis quitte
        // (captures pour la documentation, indépendantes de l'écran).
        int snap = Array.IndexOf(e.Args, "--snapshot");
        if (snap >= 0 && snap + 1 < e.Args.Length)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () => Snapshots(e.Args[snap + 1], snap + 2 < e.Args.Length ? e.Args[snap + 2] : null));
    }

    private void RunSetup(string[] args)
    {
        Storage.Load();
        Loc.Instance.Load(Storage.Settings.Language ?? Loc.SystemDefault());
        ThemeService.Apply(Storage.Settings.Theme);
        int u = Array.IndexOf(args, Installer.UpdateArg);
        var window = u >= 0 && u + 2 < args.Length && int.TryParse(args[u + 1], out var pid)
            ? new SetupWindow(SetupWindow.Mode.Update, pid, args[u + 2])
            : args.Contains(Installer.UninstallArg) ? new SetupWindow(SetupWindow.Mode.Uninstall) : new SetupWindow(SetupWindow.Mode.Install);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = window;
        if (Environment.GetEnvironmentVariable("KAIRN_SETUP_SNAPSHOT") is { Length: > 0 } snapDir)
            window.ContentRendered += async (_, _) => await window.SnapshotAsync(snapDir);
        window.Show();
    }

    private async void Snapshots(string dir, string? lang)
    {
        System.IO.Directory.CreateDirectory(dir);
        if (lang != null) Loc.Instance.Load(lang);
        var demo = System.IO.Path.Combine(Storage.Root, "assistant-dev-goal.json");
        foreach (var page in new[] { "today", "planning", "editor", "goals", "goals-preview", "library", "guard", "appearance", "settings" })
        {
            // « editor » : le planning avec la première tâche du jour ouverte dans l'éditeur.
            var first = Storage.TasksFor(DateOnly.FromDateTime(DateTime.Now)).FirstOrDefault();
            if (page == "editor") { if (first is null) continue; MainWin!.Navigate("planning", first.Date, first); }
            // « goals-preview » : l'aperçu d'un programme généré par « --assistant-demo ».
            else if (page == "goals-preview")
            {
                if (!System.IO.File.Exists(demo)) continue;
                var node = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(demo))!;
                MainWin!.Navigate("goals");
                if (MainWin.Host.Content is GoalsView gv)
                    gv.ShowPreviewForSnapshot(node["Goal"].Deserialize<Kairn.Models.Goal>()!, node["Sessions"].Deserialize<List<Kairn.Models.GoalSession>>()!);
            }
            else MainWin!.Navigate(page);
            await System.Threading.Tasks.Task.Delay(700);
            var root = (System.Windows.FrameworkElement)MainWin.Content;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                System.Windows.Media.PixelFormats.Pbgra32);
            // On dessine la fenêtre entière : c'est elle qui porte le fond et la mise en miroir des langues de droite à gauche.
            bmp.Render(MainWin);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, $"{lang ?? Loc.Instance.Current.Code}-{page}.png"));
            enc.Save(fs);
        }
        Quit();
    }

    private void BuildTrayMenu()
    {
        if (_tray?.ContextMenuStrip is not { } menu) return;
        menu.Items.Clear();
        menu.Items.Add(L.T("tray.open"), null, (_, _) => ShowMain());
        menu.Items.Add(L.T("tray.quickAdd"), null, (_, _) => QuickAddWindow.Open());
        menu.Items.Add(L.T("tray.snooze"), null, (_, _) => Guard.SnoozedUntil = DateTime.Now.AddMinutes(15));
        menu.Items.Add("-");
        menu.Items.Add(L.T("tray.quit"), null, (_, _) => Quit());
        menu.RightToLeft = Loc.Instance.Current.Rtl ? Forms.RightToLeft.Yes : Forms.RightToLeft.No;
    }

    private void SetTrayIcon(byte[] ico)
    {
        if (_tray is null) return;
        var old = _tray.Icon;
        _tray.Icon = new System.Drawing.Icon(new System.IO.MemoryStream(ico), Forms.SystemInformation.SmallIconSize);
        old?.Dispose();
    }

    public void ShowMain()
    {
        if (MainWin is null) return;
        MainWin.Show();
        if (MainWin.WindowState == WindowState.Minimized) MainWin.WindowState = WindowState.Normal;
        MainWin.Activate();
    }

    /// <summary>Les données ont changé ailleurs que dans la page affichée (capture rapide, rappel…) : elle se remet à jour.</summary>
    public void Refreshed() => (MainWin?.Host.Content as Views.IRefreshable)?.Refresh();

    private void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/kairn.ico"))?.Stream;
        _tray = new Forms.NotifyIcon
        {
            Text = "Kairn",
            Icon = iconStream != null ? new System.Drawing.Icon(iconStream) : System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        BuildTrayMenu();
        Loc.Instance.Changed += BuildTrayMenu;
        // Icône aux couleurs du thème (et mise à jour dès qu'on la change dans Apparence).
        if (AppIcon.Ico is { } ico) SetTrayIcon(ico);
        AppIcon.Changed += SetTrayIcon;
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowMain(); };
    }

    public void Quit()
    {
        Guard.Stop();
        HotKey.Unregister();
        Storage.Save();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Kairn.Services.Assistant.LocalEngine.Stop(); // le modèle local ne survit pas à Kairn
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
