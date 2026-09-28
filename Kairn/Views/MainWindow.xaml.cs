using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _views = new();
    /// <summary>Pour chaque page : l'état des données et l'heure de sa dernière mise à jour.</summary>
    private readonly Dictionary<UserControl, (long Version, DateTime At)> _seen = new();
    private string _current = "today";

    public MainWindow()
    {
        InitializeComponent();
        if (AppIcon.Window is { } icon) Icon = icon; // pierres aux couleurs du thème
        Updater.Changed += RefreshUpdate;
        RefreshUpdate();
        // Jamais plus grand que l'écran (petits écrans portables).
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 40);
        Height = Math.Min(Height, area.Height - 40);
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this, Storage.Settings.Theme.Dark);
        StateChanged += (_, _) =>
        {
            // Une fenêtre sans bordure native déborde de ~7 px une fois agrandie.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
            if (WindowState != WindowState.Minimized) RefreshCurrent();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshCurrent(); };
        Activated += (_, _) =>
        {
            RefreshCurrent();
            // On revient sur Kairn : bon moment pour redemander à iCloud ce qui a changé (au plus toutes les 3 minutes).
            CalendarSync.SyncIfIdleFor(TimeSpan.FromMinutes(3));
        };
        Loc.Instance.Changed += OnLanguageChanged;
        FlowDirection = Loc.Instance.Current.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        NavToday.IsChecked = true;
        RefreshGuard();
    }

    /// <summary>
    /// Nouvelle langue : les textes XAML suivent tout seuls ; ce qui est généré en code est reconstruit
    /// en recréant les vues (on reste sur l'onglet courant).
    /// </summary>
    private void OnLanguageChanged()
    {
        FlowDirection = Loc.Instance.Current.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _views.Clear();
        _seen.Clear();
        Navigate(_current);
        RefreshGuard();
    }

    /// <summary>
    /// Remet à jour la page affichée, seulement si les données ont changé depuis (ou si elle date de plus de 10 minutes,
    /// pour les textes qui dépendent de l'heure). Fenêtre cachée ou réduite : rien, ce sera fait à son retour.
    /// Avant, chaque retour sur la fenêtre reconstruisait toute la page.
    /// </summary>
    public void RefreshCurrent(bool force = false)
    {
        if (Host.Content is not UserControl view || view is not IRefreshable page) return;
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (!force && _seen.TryGetValue(view, out var seen) && seen.Version == Storage.Version
            && DateTime.Now - seen.At < TimeSpan.FromMinutes(10)) return;
        page.Refresh();
        _seen[view] = (Storage.Version, DateTime.Now);
    }

    private bool _syncingNav;

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingNav) return;
        if (sender is RadioButton { Tag: string key }) Navigate(key);
    }

    public void Navigate(string key, DateOnly? date = null, PlanTask? edit = null)
    {
        if (!_views.TryGetValue(key, out var view))
        {
            view = key switch
            {
                "today" => new TodayView(),
                "planning" => new PlanningView(),
                "library" => new LibraryView(),
                "goals" => new GoalsView(),
                "appearance" => new AppearanceView(),
                "guard" => new GuardView(),
                _ => new SettingsView()
            };
            _views[key] = view;
        }
        _current = key;
        if (view is PlanningView p && date is { } d) p.SelectDate(d); // qui remet déjà la page à jour
        else (view as IRefreshable)?.Refresh();
        _seen[view] = (Storage.Version, DateTime.Now);
        if (view is PlanningView pe && edit != null) pe.EditTask(edit, schedule: true);
        Host.Content = view;
        // Le menu de gauche suit toujours la page affichée (y compris quand on y arrive par un lien ou un bouton).
        if (!_syncingNav)
        {
            _syncingNav = true;
            foreach (var rb in FindNav()) rb.IsChecked = (string)rb.Tag == key;
            _syncingNav = false;
        }
    }

    private IEnumerable<RadioButton> FindNav() =>
        LogicalTreeHelper.GetChildren(NavToday.Parent).OfType<RadioButton>();

    // ===================== Liens =====================

    private void Source_Click(object sender, RoutedEventArgs e) => Launcher.Open("https://github.com/" + Updater.Repo);

    private void Kofi_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Click.Up(sender)) Launcher.Open("https://ko-fi.com/zakenoo");
    }

    // ===================== Mise à jour =====================

    private void RefreshUpdate()
    {
        var u = Updater.Available;
        UpdateCard.Visibility = u is null ? Visibility.Collapsed : Visibility.Visible;
        if (u != null) UpdateTitle.Text = L.F("update.available", Installer.VersionText(u.Version));
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Available is not { } u) return;
        UpdateBtn.IsEnabled = false;
        UpdateSub.Visibility = Visibility.Visible;
        try
        {
            await Updater.DownloadAndApplyAsync(u, new Progress<double>(f => UpdateSub.Text = L.F("update.downloading", (int)(f * 100))), CancellationToken.None);
            App.Current.Quit(); // le setup attend notre fermeture pour remplacer le fichier, puis relance Kairn
        }
        catch
        {
            UpdateSub.Text = L.T("update.failed");
            UpdateBtn.IsEnabled = true;
        }
    }

    public void RefreshGuard()
    {
        var s = Storage.Settings;
        var names = s.BlockedApps.Select(FocusGuard.DisplayName).Distinct().ToList();
        var apps = names.Count switch
        {
            0 => s.WatchFullscreen ? L.T("side.guard.fullscreen") : L.T("side.guard.noApp"),
            1 => names[0],
            2 => L.F("side.guard.two", names[0], names[1]),
            _ => $"{names[0]}, {names[1]} +{names.Count - 2}"
        };
        (GuardTitle.Text, GuardSub.Text) = s.FocusMode switch
        {
            FocusMode.Off => (L.T("side.guard.off.title"), L.T("side.guard.off.sub")),
            _ when names.Count == 0 && !s.WatchFullscreen => (L.T("side.guard.empty.title"), L.T("side.guard.empty.sub")),
            FocusMode.Gentle => (L.T("side.guard.gentle.title"), L.F("side.guard.gentle.sub", apps)),
            FocusMode.CloseAtStart => (L.T("side.guard.close.title"), L.F("side.guard.close.sub", apps)),
            _ => (L.T("side.guard.strict.title"), L.F("side.guard.strict.sub", apps))
        };
    }

    private void Press(object sender, System.Windows.Input.MouseButtonEventArgs e) => Click.Down(sender);

    private void GuardCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Click.Up(sender)) NavGuard.IsChecked = true;
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        Storage.Flush();
        if (Storage.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            TrimMemory();
            return;
        }
        base.OnClosing(e);
        App.Current.Quit();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, nint min, nint max);

    /// <summary>Une fois la fenêtre cachée, on rend la mémoire inutilisée à Windows (l'app ne garde que le garde en arrière-plan).</summary>
    private static void TrimMemory()
    {
        // Les grands blocs (images, gros fichiers lus) sont regroupés aussi, sinon leurs trous restent réservés.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);
    }
}

/// <summary>Une vue qui se met à jour quand elle redevient visible.</summary>
public interface IRefreshable
{
    void Refresh();
}
