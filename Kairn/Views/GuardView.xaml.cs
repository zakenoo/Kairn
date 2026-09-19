using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>Onglet Garde : comment réagir, et quelles apps surveiller (catalogue + apps perso).</summary>
public partial class GuardView : UserControl, IRefreshable
{
    /// <summary>Une tuile d'app : du catalogue, ajoutée à la main, ou l'option « plein écran ».</summary>
    private record Tile(string Name, string? Note, string[] Processes, string? IconPath, bool Installed, CustomApp? Custom = null, bool Fullscreen = false);

    private const string MineGroup = "guard.group.mine";
    private bool _loading = true;
    private DispatcherTimer? _gateTimer;
    private List<(CatalogApp App, bool Installed, string? Icon)> _catalog = [];

    private static AppSettings S => Storage.Settings;

    public GuardView() => InitializeComponent();

    public void Refresh()
    {
        _loading = true;
        foreach (var rb in ModeCards.Children.OfType<RadioButton>())
            rb.IsChecked = (string)rb.Tag == S.FocusMode.ToString();
        _loading = false;

        AdoptLegacyApps();
        // Détection faite une fois par ouverture de l'onglet (lecture locale du registre + processus en cours).
        InstalledApps.Reset();
        var running = InstalledApps.RunningSnapshot();
        _catalog = AppCatalog.Apps.Select(a => { var (inst, icon) = InstalledApps.Find(a, running); return (a, inst, icon); }).ToList();

        BuildTiles();
        UpdateSummary();
    }

    /// <summary>Apps ajoutées avant cet onglet (tapées à la main) : on leur crée une tuile « Mes apps ».</summary>
    private static void AdoptLegacyApps()
    {
        bool changed = false;
        foreach (var p in S.BlockedApps.ToList())
        {
            if (AppCatalog.ForProcess(p) != null || S.CustomApps.Any(c => c.Process.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
            var sys = Path.Combine(Environment.SystemDirectory, p + ".exe");
            var path = InstalledApps.RunningPath(p) ?? (File.Exists(sys) ? sys : null);
            S.CustomApps.Add(new CustomApp { Name = InstalledApps.FriendlyName(path) ?? p, Process = p, ExePath = path });
            changed = true;
        }
        if (!changed) return;
        Storage.SaveSettings();
        App.Current.MainWin?.RefreshGuard();
    }

    private void Save()
    {
        Storage.SaveSettings();
        App.Current.MainWin?.RefreshGuard();
        UpdateSummary();
    }

    // ===================== Réaction =====================

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag }) return;
        var mode = Enum.Parse<FocusMode>(tag);
        // Sortir du mode strict demande 10 secondes : de quoi casser le réflexe.
        if (S.FocusMode == FocusMode.Strict && mode != FocusMode.Strict) { StartStrictGate(mode); return; }
        S.FocusMode = mode;
        Save();
    }

    private void StartStrictGate(FocusMode target)
    {
        _gateTimer?.Stop();
        int left = 10;
        StrictGate.Visibility = Visibility.Visible;
        StrictGateText.Text = L.F("guard.gate", left);
        _gateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gateTimer.Tick += (_, _) =>
        {
            left--;
            StrictGateText.Text = L.F("guard.gate", left);
            if (left > 0) return;
            _gateTimer.Stop();
            StrictGate.Visibility = Visibility.Collapsed;
            S.FocusMode = target;
            Save();
        };
        _gateTimer.Start();
    }

    private void StrictCancel_Click(object sender, RoutedEventArgs e)
    {
        _gateTimer?.Stop();
        StrictGate.Visibility = Visibility.Collapsed;
        Refresh();
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        var g = App.Current.Guard;
        g.SnoozedUntil = g.SnoozedUntil > DateTime.Now ? DateTime.MinValue : DateTime.Now.AddMinutes(15);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var names = S.BlockedApps.Select(FocusGuard.DisplayName).Distinct().ToList();
        if (S.WatchFullscreen) names.Add(L.T("guard.fullscreenGames"));
        Summary.Text = S.FocusMode == FocusMode.Off ? L.T("guard.summary.off")
            : names.Count == 0 ? L.T("guard.summary.empty")
            : L.F("guard.summary.list", string.Join(", ", names));

        var until = App.Current.Guard.SnoozedUntil;
        SnoozeBtn.Content = until > DateTime.Now ? L.F("guard.snoozed", until.ToString("t", L.Culture)) : L.T("guard.snooze");
        SnoozeBtn.Visibility = S.FocusMode == FocusMode.Off ? Visibility.Collapsed : Visibility.Visible;
    }

    // ===================== Tuiles =====================

    private void Filter_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) BuildTiles(); }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) => BuildTiles();

    private bool IsOn(Tile t) => t.Fullscreen ? S.WatchFullscreen
        : t.Processes.Any(p => S.BlockedApps.Contains(p, StringComparer.OrdinalIgnoreCase));

    private void BuildTiles()
    {
        if (Groups is null) return;
        Groups.Children.Clear();
        var q = Search.Text.Trim();

        var tiles = new List<(string Group, Tile Tile)>();
        foreach (var c in S.CustomApps)
            tiles.Add((MineGroup, new Tile(c.Name, L.T("guard.addedByYou"), [c.Process], c.ExePath, true, c)));
        tiles.Add((AppCatalog.Games, new Tile(L.T("guard.fullscreen"), L.T("guard.fullscreen.note"), [], null, true, Fullscreen: true)));
        foreach (var (app, installed, icon) in _catalog)
            tiles.Add((app.Group, new Tile(app.Name, app.Note is null ? null : L.T(app.Note), app.Processes, icon, installed)));

        bool onlyInstalled = FilterInstalled.IsChecked == true, onlyOn = FilterOn.IsChecked == true;
        int shown = 0;
        foreach (var group in new[] { MineGroup }.Concat(AppCatalog.Groups))
        {
            var items = tiles.Where(t => t.Group == group).Select(t => t.Tile)
                .Where(t => q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Where(t => !onlyOn || IsOn(t))
                .Where(t => !onlyInstalled || t.Installed || IsOn(t) || q.Length > 0)
                .OrderByDescending(t => t.Fullscreen).ThenByDescending(t => t.Installed).ThenBy(t => t.Name)
                .ToList();
            if (items.Count == 0) continue;

            var header = new TextBlock { Text = L.T(group).ToUpper(L.Culture), Margin = new Thickness(2, 6, 0, 8) };
            header.SetResourceReference(StyleProperty, "Label");
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var t in items) wrap.Children.Add(TileView(t));
            Groups.Children.Add(header);
            Groups.Children.Add(wrap);
            shown += items.Count;
        }

        if (shown == 0)
        {
            var empty = new TextBlock
            {
                Text = onlyOn ? L.T("guard.empty.on") : L.T("guard.empty.search"),
                Margin = new Thickness(2, 4, 0, 16), TextWrapping = TextWrapping.Wrap
            };
            empty.SetResourceReference(StyleProperty, "Subtle");
            Groups.Children.Add(empty);
        }
    }

    private FrameworkElement TileView(Tile t)
    {
        bool on = IsOn(t);

        // Icône réelle de l'app si on la trouve, sinon une pastille avec son initiale.
        FrameworkElement icon;
        if (InstalledApps.Icon(t.IconPath) is { } img)
            icon = new Image { Source = img, Width = 30, Height = 30 };
        else
        {
            var letter = new TextBlock
            {
                Text = t.Fullscreen ? "" : t.Name[..1].ToUpperInvariant(),
                FontWeight = FontWeights.SemiBold, FontSize = t.Fullscreen ? 15 : 14,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            if (t.Fullscreen) letter.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            letter.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var pill = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Child = letter };
            pill.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            icon = pill;
        }
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        if (!t.Installed && !on) icon.Opacity = 0.5;

        var name = new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock
        {
            Text = t.Note ?? L.T(t.Installed ? "guard.installed" : "guard.notDetected"),
            FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), ToolTip = t.Note
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, sub } };

        // Interrupteur visuel (toute la tuile est cliquable).
        var knob = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(3, 0, 3, 0),
                                HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        knob.SetResourceReference(Border.BackgroundProperty, on ? "OnAccentBrush" : "SubtleBrush");
        var track = new Border { Width = 34, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = knob,
                                 VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        track.SetResourceReference(Border.BackgroundProperty, on ? "AccentBrush" : "Surface2Brush");
        track.SetResourceReference(Border.BorderBrushProperty, on ? "AccentBrush" : "LineBrush");

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(track, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(track);
        row.Children.Add(texts);

        var card = new Border
        {
            Width = 262, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Cursor = System.Windows.Input.Cursors.Hand,
            Child = row, ToolTip = t.Processes.Length > 0 ? L.F("guard.program", string.Join(", ", t.Processes.Select(p => p + ".exe"))) : null
        };
        card.SetResourceReference(Border.BackgroundProperty, on ? "AccentSoftBrush" : "ZoneRowBrush");
        card.SetResourceReference(Border.BorderBrushProperty, on ? "AccentBrush" : "LineBrush");
        Click.Attach(card, () => Toggle(t));

        if (t.Custom is null) return card;

        // Les apps perso peuvent être retirées complètement.
        var del = new Button { Content = "", Width = 22, Height = 22, FontSize = 8, ToolTip = L.T("guard.removeApp"),
                               HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, 4, 0) };
        del.SetResourceReference(StyleProperty, "IconButton");
        del.Click += (_, e) =>
        {
            e.Handled = true;
            S.CustomApps.Remove(t.Custom);
            S.BlockedApps.RemoveAll(p => p.Equals(t.Custom.Process, StringComparison.OrdinalIgnoreCase));
            Save();
            BuildTiles();
        };
        return new Grid { Children = { card, del } };
    }

    private void Toggle(Tile t)
    {
        if (t.Fullscreen) S.WatchFullscreen = !S.WatchFullscreen;
        else if (IsOn(t)) S.BlockedApps.RemoveAll(p => t.Processes.Contains(p, StringComparer.OrdinalIgnoreCase));
        else S.BlockedApps.AddRange(t.Processes.Where(p => !S.BlockedApps.Contains(p, StringComparer.OrdinalIgnoreCase)));
        Save();
        BuildTiles();
    }

    // ===================== Autre app =====================

    private void AddCustom(string name, string process, string? exePath)
    {
        if (process.Equals("Kairn", StringComparison.OrdinalIgnoreCase) || process.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return;

        // Déjà dans le catalogue ? On active simplement sa tuile.
        if (AppCatalog.ForProcess(process) is { } known)
            S.BlockedApps.AddRange(known.Processes.Where(p => !S.BlockedApps.Contains(p, StringComparer.OrdinalIgnoreCase)));
        else
        {
            if (!S.CustomApps.Any(c => c.Process.Equals(process, StringComparison.OrdinalIgnoreCase)))
                S.CustomApps.Add(new CustomApp { Name = name, Process = process, ExePath = exePath });
            if (!S.BlockedApps.Contains(process, StringComparer.OrdinalIgnoreCase)) S.BlockedApps.Add(process);
        }
        Save();
        FilterOn.IsChecked = true; // montre tout de suite ce qui est surveillé, dont l'app ajoutée
        BuildTiles();
    }

    private void PickOpen_Click(object sender, RoutedEventArgs e)
    {
        if (OpenApps.Visibility == Visibility.Visible)
        {
            OpenApps.Visibility = Visibility.Collapsed;
            PickOpenText.Text = L.T("guard.pickOpen");
            return;
        }
        OpenApps.Children.Clear();
        foreach (var (name, process, path) in InstalledApps.OpenWindows())
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (InstalledApps.Icon(path) is { } img) content.Children.Add(new Image { Source = img, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) });
            content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = content, Margin = new Thickness(0, 0, 8, 8), ToolTip = process + ".exe" };
            b.Click += (_, _) => { AddCustom(name, process, path); b.IsEnabled = false; };
            OpenApps.Children.Add(b);
        }
        if (OpenApps.Children.Count == 0)
        {
            var none = new TextBlock { Text = L.T("guard.noWindows") };
            none.SetResourceReference(StyleProperty, "Subtle");
            OpenApps.Children.Add(none);
        }
        OpenApps.Visibility = Visibility.Visible;
        PickOpenText.Text = L.T("guard.hideList");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = L.T("dlg.programs") + " (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (dlg.ShowDialog() != true) return;
        var process = Path.GetFileNameWithoutExtension(dlg.FileName);
        AddCustom(InstalledApps.FriendlyName(dlg.FileName) ?? process, process, dlg.FileName);
    }
}
