using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// Le setup de Kairn : bienvenue (langue) → ambiance (thème appliqué en direct) → réglages → installation.
/// Sert aussi aux mises à jour (lancé par Kairn) et à la désinstallation (depuis Paramètres Windows).
/// </summary>
public partial class SetupWindow : Window
{
    public enum Mode { Install, Update, Uninstall }

    private readonly Mode _mode;
    private readonly int _updatePid;
    private readonly string? _updateTarget;
    private readonly bool _fresh = !Storage.HasSettings;
    private ThemeDef _theme;
    private FrameworkElement? _page;
    private bool _busy;
    private Action? _next, _alt;
    private int _stones;

    public SetupWindow(Mode mode, int updatePid = 0, string? updateTarget = null)
    {
        InitializeComponent();
        _mode = mode;
        _updatePid = updatePid;
        _updateTarget = updateTarget;
        _theme = _fresh ? ThemePresets.All[0].Clone() : Storage.Settings.Theme.Clone();
        VersionText.Text = L.F("set.updates.version", Installer.VersionText(Installer.CurrentVersion));
        WhereText.Text = L.F("setup.prefs.where", Installer.InstallDir);
        if (AppIcon.Window is { } icon) Icon = icon;
        Loc.Instance.Changed += OnLanguage;
        Closed += (_, _) => Loc.Instance.Changed -= OnLanguage;
        Loaded += (_, _) => Start();
    }

    private void Start()
    {
        switch (_mode)
        {
            case Mode.Update:
                Steps.Visibility = Visibility.Collapsed;
                _ = RunUpdateAsync();
                break;
            case Mode.Uninstall:
                Steps.Visibility = Visibility.Collapsed;
                BuildCairn(3, animate: true);
                GoUninstall();
                break;
            default:
                BuildLanguages();
                BuildCairn(3, animate: true);
                GoWelcome();
                break;
        }
    }

    // ===================== Étapes =====================

    private void GoWelcome()
    {
        Show(PageWelcome, 0, forward: true);
        Nav(back: null, next: (L.T("setup.start"), () =>
        {
            if (!_fresh || Installer.IsInstalled) GoExisting();
            else GoTheme();
        }));
    }

    private void GoExisting()
    {
        var installed = Installer.InstalledVersion;
        ExistingText.Text = L.F("setup.existing.sub",
            installed is null ? "—" : Installer.VersionText(installed), Installer.VersionText(Installer.CurrentVersion));
        Show(PageExisting, 1, forward: true);
        Nav(back: GoWelcome, next: (L.T("setup.update"), () => _ = RunInstallAsync(applyChoices: false)));
    }

    private void Reinstall_Click(object sender, RoutedEventArgs e) => GoTheme();

    private void GoTheme()
    {
        BuildThemes();
        Show(PageTheme, 1, forward: true);
        Nav(back: GoWelcome, next: (L.T("setup.next"), GoPrefs));
    }

    private void GoPrefs()
    {
        if (!_fresh)
        {
            NameBox.Text = Storage.Settings.UserName ?? "";
            UpdatesSwitch.IsChecked = Storage.Settings.CheckUpdates || _fresh;
        }
        Show(PagePrefs, 2, forward: true);
        Nav(back: GoTheme, next: (L.T("setup.install"), () => _ = RunInstallAsync(applyChoices: true)));
    }

    private void GoUninstall()
    {
        Show(PageUninstall, -1, forward: true);
        Nav(back: null, next: (L.T("uninstall.go"), () => _ = RunUninstallAsync()), alt: (L.T("common.cancel"), Close));
    }

    private void Nav(Action? back, (string Text, Action Do)? next, (string Text, Action Do)? alt = null)
    {
        BackBtn.Visibility = back is null ? Visibility.Collapsed : Visibility.Visible;
        _back = back;
        NextBtn.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        if (next is { } n) { NextBtn.Content = n.Text; _next = n.Do; }
        AltBtn.Visibility = alt is null ? Visibility.Collapsed : Visibility.Visible;
        if (alt is { } a) { AltBtn.Content = a.Text; _alt = a.Do; }
        ErrorText.Text = "";
    }

    private Action? _back;
    private void Back_Click(object sender, RoutedEventArgs e) => _back?.Invoke();
    private void Next_Click(object sender, RoutedEventArgs e) => _next?.Invoke();
    private void Alt_Click(object sender, RoutedEventArgs e) => _alt?.Invoke();

    // ===================== Travail =====================

    private async Task RunInstallAsync(bool applyChoices)
    {
        if (_busy) return;
        _busy = true;
        CloseBtn.IsEnabled = false;
        var started = DateTime.Now;
        WorkTitle.Text = L.T(applyChoices ? "setup.installing" : "setup.updatingInstall");
        Show(PageWork, 3, forward: true);
        Nav(null, null);
        BuildCairn(0, animate: false);

        if (applyChoices)
        {
            // Tout est prêt au premier lancement : thème, langue, prénom, démarrage, mises à jour.
            var s = Storage.Settings;
            s.Theme = _theme.Clone();
            s.Language = Loc.Instance.Current.Code;
            var name = NameBox.Text.Trim();
            s.UserName = name.Length == 0 ? null : name;
            s.StartWithWindows = StartupSwitch.IsChecked == true;
            s.CheckUpdates = UpdatesSwitch.IsChecked == true;
            Storage.SaveSettings();
        }

        try
        {
            var options = applyChoices ? new Installer.Options(StartupSwitch.IsChecked == true, DesktopSwitch.IsChecked == true) : null;
            var progress = Progress(); // créé ici, sur le fil de l'interface
            await Task.Run(() => Installer.InstallAsync(options, progress));
            // Qu'on ait le temps de voir le cairn se construire, même sur un disque rapide.
            var remaining = TimeSpan.FromSeconds(2.4) - (DateTime.Now - started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            ShowDone(L.F("setup.done.title", Storage.Settings.UserName is { Length: > 0 } n ? ", " + n : ""), L.T("setup.done.sub"),
                     (L.T("setup.open"), () => { Launch(Installer.InstalledExe); Close(); }));
        }
        catch (Exception ex)
        {
            _busy = false;
            CloseBtn.IsEnabled = true;
            if (applyChoices) GoPrefs(); else GoExisting();
            ErrorText.Text = L.F("setup.error", ex.Message);
        }
    }

    private async Task RunUpdateAsync()
    {
        _busy = true;
        CloseBtn.IsEnabled = false;
        BuildCairn(0, animate: false);
        WorkTitle.Text = L.F("setup.updating", Installer.VersionText(Installer.CurrentVersion));
        Show(PageWork, -1, forward: true);
        Nav(null, null);
        var target = _updateTarget ?? Installer.InstalledExe;
        try
        {
            // Kairn vient de se fermer pour laisser la place : on attend qu'il ait bien disparu.
            await Task.Run(() =>
            {
                try { using var p = Process.GetProcessById(_updatePid); if (!p.WaitForExit(15000)) p.Kill(); } catch { }
            });
            var progress = Progress();
            await Task.Run(() => Installer.InstallAsync(null, progress, target));
            await Task.Delay(700);
            Launch(target);
            Close();
        }
        catch (Exception ex)
        {
            _busy = false;
            CloseBtn.IsEnabled = true;
            ShowDone(L.T("setup.err.title"), L.F("setup.error", ex.Message), (L.T("setup.close"), Close));
        }
    }

    private async Task RunUninstallAsync()
    {
        _busy = true;
        CloseBtn.IsEnabled = false;
        WorkTitle.Text = L.T("uninstall.working");
        WorkStep.Text = "";
        Show(PageWork, -1, forward: true);
        Nav(null, null);
        bool deleteData = DeleteDataSwitch.IsChecked == true;
        AnimateBar(0.3);
        // Les pierres s'en vont une à une.
        for (int i = Cairn.Children.Count - 1; i >= 0; i--)
        {
            Cairn.Children[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)));
            await Task.Delay(220);
        }
        await Task.Run(() => Installer.Uninstall(deleteData));
        AnimateBar(1);
        await Task.Delay(400);
        ShowDone(L.T("uninstall.done"), L.T("uninstall.done.sub"), (L.T("setup.close"), Close));
    }

    private IProgress<(string Step, double Fraction)> Progress() => new Progress<(string Step, double Fraction)>(p =>
    {
        WorkStep.Text = L.T("setup.progress." + p.Step);
        AnimateBar(p.Fraction);
        // Une pierre de plus à chaque étape franchie.
        int want = p.Fraction >= 0.9 ? 3 : p.Fraction >= 0.5 ? 2 : p.Fraction >= 0.1 ? 1 : 0;
        while (_stones < want) AddStone(_stones, animate: true);
    });

    private void ShowDone(string title, string text, (string, Action) next)
    {
        _busy = false;
        CloseBtn.IsEnabled = true;
        DoneTitle.Text = title;
        DoneText.Text = text;
        Show(PageDone, -1, forward: true);
        Nav(null, next, _mode == Mode.Install && Installer.IsInstalled ? (L.T("setup.close"), Close) : null);
        var pulse = new DoubleAnimation(1, 1.06, TimeSpan.FromMilliseconds(260)) { AutoReverse = true, EasingFunction = new SineEase() };
        var st = new ScaleTransform(1, 1, Cairn.Width / 2, Cairn.Height);
        Cairn.RenderTransform = st;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    private static void Launch(string exe)
    {
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(exe)! }); } catch { }
    }

    private void AnimateBar(double fraction)
    {
        if (WorkBar.Parent is not Grid g) return;
        double w = Math.Max(1, g.ActualWidth > 0 ? g.ActualWidth : 480) * Math.Clamp(fraction, 0, 1);
        WorkBar.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase() });
    }

    // ===================== Le cairn =====================

    private static readonly (double W, double H, double Y, double Opacity)[] StoneShapes =
        [(132, 34, 110, 1.0), (98, 28, 85, 0.86), (64, 22, 65, 0.72)];

    private void BuildCairn(int count, bool animate)
    {
        Cairn.Children.Clear();
        Cairn.RenderTransform = null;
        _stones = 0;
        for (int i = 0; i < count; i++) AddStone(i, animate, delayMs: i * 260);
    }

    private void AddStone(int i, bool animate, int delayMs = 0)
    {
        if (i >= StoneShapes.Length) return;
        var (w, h, y, op) = StoneShapes[i];
        var stone = new Ellipse { Width = w, Height = h, Opacity = animate ? 0 : op };
        stone.SetResourceReference(Shape.FillProperty, "AccentBrush");
        Canvas.SetLeft(stone, (Cairn.Width - w) / 2 + (i == 1 ? 3 : i == 2 ? -2 : 0));
        Canvas.SetTop(stone, y);
        Cairn.Children.Add(stone);
        _stones = Math.Max(_stones, i + 1);
        if (!animate) return;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var tt = new TranslateTransform(0, -70);
        stone.RenderTransform = tt;
        stone.BeginAnimation(OpacityProperty, new DoubleAnimation(0, op, TimeSpan.FromMilliseconds(300)) { BeginTime = begin });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-70, 0, TimeSpan.FromMilliseconds(650))
        { BeginTime = begin, EasingFunction = new BounceEase { Bounces = 1, Bounciness = 3.5, EasingMode = EasingMode.EaseOut } });
    }

    // ===================== Pages et étapes (animations) =====================

    private void Show(FrameworkElement page, int stepIndex, bool forward)
    {
        var old = _page;
        _page = page;
        if (old != null && old != page)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) => { if (_page != old) old.Visibility = Visibility.Collapsed; };
            old.BeginAnimation(OpacityProperty, fade);
        }
        page.Visibility = Visibility.Visible;
        var tt = new TranslateTransform(forward ? 34 : -34, 0);
        page.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(old != null && old != page ? 110 : 0);
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(tt.X, 0, TimeSpan.FromMilliseconds(380)) { BeginTime = begin, EasingFunction = ease });
        if (stepIndex >= 0) BuildSteps(stepIndex);
    }

    private int _step;

    private void BuildSteps(int active)
    {
        _step = active;
        Steps.Children.Clear();
        var keys = new[] { "setup.step.welcome", "setup.step.theme", "setup.step.prefs", "setup.step.install" };
        for (int i = 0; i < keys.Length; i++)
        {
            bool done = i < active, now = i == active;
            var dot = new Border { Width = now ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            dot.SetResourceReference(Border.BackgroundProperty, now || done ? "AccentBrush" : "Surface2Brush");
            if (now) dot.BeginAnimation(WidthProperty, new DoubleAnimation(8, 22, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase() });
            var label = new TextBlock { Text = L.T(keys[i]), FontSize = 13.5, FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, now ? "TextBrush" : done ? "SubtleBrush" : "FaintBrush");
            Steps.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14), Children = { dot, label } });
        }
    }

    // ===================== Langue =====================

    private void BuildLanguages()
    {
        LangChips.Children.Clear();
        foreach (var lang in Loc.Languages)
        {
            var rb = new RadioButton
            {
                Content = lang.NativeName, GroupName = "setupLang", IsChecked = lang.Code == Loc.Instance.Current.Code,
                FlowDirection = lang.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            };
            rb.SetResourceReference(StyleProperty, "Chip");
            rb.Checked += (_, _) => { if (lang.Code != Loc.Instance.Current.Code) Dispatcher.BeginInvoke(() => Loc.Instance.Load(lang.Code)); };
            LangChips.Children.Add(rb);
        }
    }

    private void OnLanguage()
    {
        FlowDirection = Loc.Instance.Current.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        VersionText.Text = L.F("set.updates.version", Installer.VersionText(Installer.CurrentVersion));
        WhereText.Text = L.F("setup.prefs.where", Installer.InstallDir);
        if (_page == PageWelcome) { NextBtn.Content = L.T("setup.start"); BuildSteps(_step); }
    }

    // ===================== Ambiance =====================

    private void BuildThemes()
    {
        ThemeGrid.Children.Clear();
        foreach (var p in ThemePresets.All)
        {
            bool selected = p.Name == _theme.Name;
            SolidColorBrush B(string key) => new(ThemeService.Parse(p.Color(key), ThemeTokens.Fallback(key)));

            var preview = new Grid { Height = 82 };
            preview.Children.Add(new Border { Background = B("Bg"), CornerRadius = new CornerRadius(9) });
            preview.Children.Add(new Border { Background = B("Surface"), Width = 24, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(9, 0, 0, 9) });
            var card = new Border
            {
                Background = B("Surface"), BorderBrush = B("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Math.Min(p.Radius, 10) / 2), Margin = new Thickness(32, 11, 11, 11), Padding = new Thickness(9, 7, 9, 7),
                Child = new StackPanel
                {
                    Children =
                    {
                        new Border { Height = 5, Width = 56, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = B("Text") },
                        new Border { Height = 4, Width = 72, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = B("Subtle"), Margin = new Thickness(0, 5, 0, 0) },
                        new Border { Height = 11, Width = 36, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Background = B("Accent"), Margin = new Thickness(0, 8, 0, 0) },
                    }
                }
            };
            preview.Children.Add(card);
            var name = new TextBlock { Text = AppearanceView.DisplayName(p), FontSize = 12.5, Margin = new Thickness(2, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var outer = new Border
            {
                Width = 146, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(6), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12), Background = Brushes.Transparent,
                Child = new StackPanel { Children = { preview, name } },
            };
            outer.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "LineBrush");
            var theme = p;
            Click.Attach(outer, () => PickTheme(theme));
            ThemeGrid.Children.Add(outer);
        }
    }

    /// <summary>Le setup prend tout de suite les couleurs choisies : on voit l'ambiance avant d'installer.</summary>
    private void PickTheme(ThemeDef p)
    {
        _theme = p.Clone();
        ThemeService.Apply(_theme);
        BuildThemes();
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase() });
    }

    // ===================== Fenêtre =====================

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }
    // ===================== Captures (développement) =====================

    /// <summary>« KAIRN_SETUP_SNAPSHOT=dossier » : enregistre une image de chaque écran, sans rien installer.</summary>
    internal async Task SnapshotAsync(string dir)
    {
        if (Environment.GetEnvironmentVariable("KAIRN_SNAPSHOT_LANG") is { Length: > 0 } lang) { Loc.Instance.Load(lang); BuildLanguages(); GoWelcome(); }
        System.IO.Directory.CreateDirectory(dir);
        async Task Shot(string name)
        {
            await Task.Delay(900);
            var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(this);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        if (_mode == Mode.Uninstall) { await Shot("uninstall"); Close(); return; }
        await Shot("1-welcome");
        GoTheme(); await Shot("2-theme");
        PickTheme(ThemePresets.All[3]); await Shot("3-theme-picked");
        GoPrefs(); await Shot("4-prefs");
        WorkTitle.Text = L.T("setup.installing"); Show(PageWork, 3, forward: true); Nav(null, null); BuildCairn(0, false);
        var p = Progress(); p.Report(("copy", 0.6)); await Shot("5-install");
        ShowDone(L.F("setup.done.title", ", Zak"), L.T("setup.done.sub"), (L.T("setup.open"), Close)); await Shot("6-done");
        Close();
    }
}
