using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _views = new();

    public MainWindow()
    {
        InitializeComponent();
        if (AppIcon.Window is { } icon) Icon = icon; // pierres aux couleurs du thème
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
        };
        Activated += (_, _) => (Host.Content as IRefreshable)?.Refresh();
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
        var current = _views.FirstOrDefault(kv => kv.Value == Host.Content).Key ?? "today";
        _views.Clear();
        Navigate(current);
        RefreshGuard();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
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
        if (view is PlanningView p && date is { } d) p.SelectDate(d);
        (view as IRefreshable)?.Refresh();
        if (view is PlanningView pe && edit != null) pe.EditTask(edit, schedule: true);
        Host.Content = view;
        if (key == "planning" && date != null)
            foreach (var rb in FindNav()) rb.IsChecked = (string)rb.Tag == "planning";
    }

    private IEnumerable<RadioButton> FindNav() =>
        LogicalTreeHelper.GetChildren(NavToday.Parent).OfType<RadioButton>();

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
        Storage.Save();
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
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
    }
}

/// <summary>Une vue qui se met à jour quand elle redevient visible.</summary>
public interface IRefreshable
{
    void Refresh();
}
