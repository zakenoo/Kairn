using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

public partial class AppearanceView : UserControl, IRefreshable
{
    private static readonly string[] CuratedFonts =
        ["Segoe UI Variable Display", "Segoe UI", "Bahnschrift", "Georgia", "Cascadia Code", "Consolas", "Palatino Linotype", "Trebuchet MS"];

    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private List<string>? _systemFonts;
    private Action<Color>? _pickerTarget;
    private bool _loading = true; // vrai jusqu'au premier Refresh : les curseurs déclenchent ValueChanged dès InitializeComponent
    private bool _scaleDragging;

    private static ThemeDef T => Storage.Settings.Theme;

    public AppearanceView()
    {
        InitializeComponent();
        // Les changements en rafale (glisser dans le sélecteur de couleur) sont regroupés.
        _applyTimer.Tick += (_, _) =>
        {
            _applyTimer.Stop();
            ThemeService.Apply(T);
            Storage.SaveSettings();
            UpdateIconPreview(); // l'icône suit aussi la couleur d'accent et le fond
        };
        Picker.ColorChanged += c => _pickerTarget?.Invoke(c);
        ScaleSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _scaleDragging = true));
    }

    private void ScheduleApply()
    {
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    /// <summary>Applique tout de suite et reconstruit l'éditeur (après un changement de thème complet).</summary>
    private void ApplyNow()
    {
        ThemeService.Apply(T);
        Storage.SaveSettings();
        Refresh();
    }

    public void Refresh()
    {
        _loading = true;
        BuildPresets();
        BuildCustom();
        BuildColors();
        BuildIcon();
        BuildZones();
        BuildFonts();
        DarkSwitch.IsChecked = T.Dark;
        ScaleSlider.Value = T.UiScale;
        RadiusSlider.Value = T.Radius;
        BorderSlider.Value = T.BorderWidth;
        UpdateShapeTexts();
        _loading = false;
    }

    // ===================== Galerie de thèmes =====================

    private void BuildPresets()
    {
        PresetList.Children.Clear();
        foreach (var p in ThemePresets.All)
            PresetList.Children.Add(ThemeCard(p, () => { Storage.Settings.Theme = p.Clone(); ApplyNow(); }, null));
    }

    private void BuildCustom()
    {
        CustomList.Children.Clear();
        foreach (var c in Storage.Settings.CustomThemes.ToList())
            CustomList.Children.Add(ThemeCard(c, () => { Storage.Settings.Theme = c.Clone(); ApplyNow(); },
                () => { Storage.Settings.CustomThemes.Remove(c); Storage.SaveSettings(); BuildCustom(); }));
        NoCustom.Visibility = Storage.Settings.CustomThemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Vignette d'un thème : un mini aperçu dessiné avec ses propres couleurs.</summary>
    private FrameworkElement ThemeCard(ThemeDef t, Action apply, Action? delete)
    {
        SolidColorBrush B(string key) => new(ThemeService.Parse(t.Color(key), ThemeTokens.Fallback(key)));
        bool active = t.Name == T.Name;

        var preview = new Grid { Height = 74 };
        preview.Children.Add(new Border { Background = B("Bg"), CornerRadius = new CornerRadius(8) });
        preview.Children.Add(new Border { Background = B("Surface"), Width = 22, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(8, 0, 0, 8) });
        var card = new Border
        {
            Background = B("Surface"), BorderBrush = B("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Math.Min(t.Radius, 10) / 2), Margin = new Thickness(30, 10, 10, 10), Padding = new Thickness(8, 6, 8, 6)
        };
        var lines = new StackPanel();
        lines.Children.Add(new Border { Height = 5, Width = 54, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = B("Text") });
        lines.Children.Add(new Border { Height = 4, Width = 70, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = B("Subtle"), Margin = new Thickness(0, 5, 0, 0) });
        lines.Children.Add(new Border { Height = 10, Width = 34, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Background = B("Accent"), Margin = new Thickness(0, 7, 0, 0) });
        card.Child = lines;
        preview.Children.Add(card);

        var name = new TextBlock { Text = DisplayName(t), FontSize = 12.5, Margin = new Thickness(2, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var body = new StackPanel { Children = { preview, name } };

        var outer = new Border
        {
            Width = 150, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(6), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Child = body,
            ToolTip = L.T("app.apply")
        };
        outer.SetResourceReference(Border.BorderBrushProperty, active ? "AccentBrush" : "LineBrush");
        Click.Attach(outer, apply);

        if (delete is null) return outer;
        var del = new Button { Content = "", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                               Width = 24, Height = 24, FontSize = 9, Margin = new Thickness(0, -2, -2, 0), ToolTip = L.T("app.deleteTheme") };
        del.SetResourceReference(StyleProperty, "IconButton");
        del.Click += (_, e) => { e.Handled = true; delete(); };
        return new Grid { Children = { outer, del } };
    }

    /// <summary>Nom affiché : traduit pour un thème préfait, tel quel pour un thème perso.</summary>
    private static string DisplayName(ThemeDef t)
    {
        int i = ThemePresets.All.FindIndex(p => p.Name == t.Name);
        return i >= 0 ? L.T("theme.preset." + i) : t.Name;
    }

    private void SaveTheme_Click(object sender, RoutedEventArgs e)
    {
        var name = ThemeName.Text.Trim();
        if (name.Length == 0) name = L.F("app.myThemeN", Storage.Settings.CustomThemes.Count + 1);
        T.Name = name;
        Storage.Settings.CustomThemes.RemoveAll(c => c.Name == name);
        Storage.Settings.CustomThemes.Add(T.Clone());
        Storage.SaveSettings();
        ThemeName.Text = "";
        BuildCustom();
        BuildPresets();
    }

    private void ThemeName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveTheme_Click(sender, e);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = L.T("dlg.theme") + " (*.kairntheme)|*.kairntheme", FileName = DisplayName(T) + ".kairntheme" };
        if (dlg.ShowDialog() == true) ThemeService.Export(T, dlg.FileName);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = L.T("dlg.theme") + " (*.kairntheme)|*.kairntheme" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var t = ThemeService.Import(dlg.FileName);
            if (t is null) return;
            Storage.Settings.CustomThemes.RemoveAll(c => c.Name == t.Name);
            Storage.Settings.CustomThemes.Add(t);
            Storage.Settings.Theme = t.Clone();
            ApplyNow();
        }
        catch { /* archive illisible */ }
    }

    // ===================== Couleurs =====================

    private void BuildColors()
    {
        ColorsGrid.Children.Clear();
        foreach (var (key, _) in ThemeTokens.ColorKeys)
        {
            var row = ColorRow(L.T("theme.color." + key), () => T.Color(key), c => { T.Colors[key] = ThemeService.ToHex(c); ScheduleApply(); }, null);
            row.Margin = new Thickness(0, 0, 16, 8);
            ColorsGrid.Children.Add(row);
        }
    }

    /// <summary>Ligne « pastille + nom + code » ; un clic sur la pastille ouvre le sélecteur.</summary>
    private FrameworkElement ColorRow(string label, Func<string> get, Action<Color> set, Action? reset)
    {
        var swatch = new Border
        {
            Width = 34, Height = 26, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(ThemeService.Parse(get(), "#000000")), ToolTip = L.T("app.changeColor")
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var hex = new TextBlock { Text = get(), FontSize = 11.5, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
        hex.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");

        Click.Attach(swatch, () => OpenPicker(swatch, ThemeService.Parse(get(), "#000000"), c =>
        {
            swatch.Background = new SolidColorBrush(c);
            hex.Text = ThemeService.ToHex(c);
            set(c);
        }));

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(swatch, Dock.Left);
        panel.Children.Add(swatch);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(right, Dock.Right);
        right.Children.Add(hex);
        if (reset != null)
        {
            var auto = new Button { Content = L.T("app.auto"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), FontSize = 11.5, ToolTip = L.T("app.autoTip") };
            auto.SetResourceReference(StyleProperty, "Ghost");
            auto.Click += (_, _) => reset();
            right.Children.Add(auto);
        }
        panel.Children.Add(right);
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    // ===================== Icône =====================

    private void BuildIcon()
    {
        IconColors.Children.Clear();
        var stones = ColorRow(L.T("app.icon.stones"), () => T.IconStones ?? T.Color("Accent"),
            c => { T.IconStones = ThemeService.ToHex(c); UpdateIconPreview(); ScheduleApply(); },
            () => { T.IconStones = null; ScheduleApply(); BuildIcon(); });
        var tile = ColorRow(L.T("app.icon.tile"), () => T.IconTile ?? T.Color("Bg"),
            c => { T.IconTile = ThemeService.ToHex(c); UpdateIconPreview(); ScheduleApply(); },
            () => { T.IconTile = null; ScheduleApply(); BuildIcon(); });
        stones.Margin = tile.Margin = new Thickness(0, 0, 16, 8);
        IconColors.Children.Add(stones);
        IconColors.Children.Add(tile);
        UpdateIconPreview();
    }

    private void UpdateIconPreview() => IconPreview.Source = AppIcon.Render(128, AppIcon.StonesColor(T), AppIcon.TileColor(T));

    private void OpenPicker(FrameworkElement anchor, Color current, Action<Color> onChange)
    {
        _pickerTarget = null;
        Picker.Color = current;
        _pickerTarget = onChange;
        ColorPopup.PlacementTarget = anchor;
        ColorPopup.IsOpen = true;
    }

    private void DarkSwitch_Click(object sender, RoutedEventArgs e)
    {
        T.Dark = DarkSwitch.IsChecked == true;
        ScheduleApply();
    }

    // ===================== Blocs =====================

    private void BuildZones()
    {
        ZonesPanel.Children.Clear();
        foreach (var (key, _, defaultColor) in ThemeTokens.ZoneKeys)
            ZonesPanel.Children.Add(ZoneRow(key, L.T("theme.zone." + key), defaultColor));
    }

    private FrameworkElement ZoneRow(string key, string label, string defaultColor)
    {
        var z = T.Zone(key);

        // Aperçu en direct : utilise la même ressource que le vrai bloc.
        var preview = new Border { Width = 96, Height = 64, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 16, 0) };
        preview.SetResourceReference(Border.BackgroundProperty, "Zone" + key + "Brush");
        preview.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

        var title = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 8),
            Text = z.Image != null ? L.F("app.zone.image", z.Image) : L.T(z.Color != null ? "app.zone.custom" : "app.zone.theme") };
        status.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");

        var colorRow = ColorRow(L.T(z.Image != null ? "app.zone.veilColor" : "app.zone.color"), () => z.Color ?? T.Color(defaultColor),
            c => { z.Color = ThemeService.ToHex(c); ScheduleApply(); },
            z.Color != null ? () => { z.Color = null; ApplyNow(); } : null);

        var imageBtn = new Button { Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        imageBtn.Content = L.T(z.Image != null ? "app.zone.changeImage" : "app.zone.addImage");
        imageBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = L.T("dlg.images") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff" };
            if (dlg.ShowDialog() != true) return;
            try { z.Image = ThemeService.ImportImage(dlg.FileName); } catch { return; }
            ApplyNow();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { imageBtn } };
        if (z.Image != null)
        {
            var remove = new Button { Content = L.T("app.zone.removeImage"), Padding = new Thickness(12, 6, 12, 6) };
            remove.SetResourceReference(StyleProperty, "Ghost");
            remove.Click += (_, _) => { z.Image = null; ApplyNow(); };
            buttons.Children.Add(remove);
        }

        var sliders = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        sliders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        sliders.ColumnDefinitions.Add(new ColumnDefinition());
        sliders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        int row = 0;
        if (z.Image != null)
            AddSlider(sliders, ref row, L.T("app.zone.veil"), z.Veil, v => { z.Veil = v; ScheduleApply(); });
        AddSlider(sliders, ref row, L.T("app.zone.opacity"), z.Opacity, v => { z.Opacity = v; ScheduleApply(); });

        var details = new StackPanel { Children = { title, status, colorRow, buttons, sliders } };
        var grid = new DockPanel();
        DockPanel.SetDock(preview, Dock.Left);
        grid.Children.Add(preview);
        grid.Children.Add(details);

        var card = new Border { Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 10), Child = grid };
        card.SetResourceReference(StyleProperty, "Card");
        return card;
    }

    private static void AddSlider(Grid g, ref int row, string label, double value, Action<double> set)
    {
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var pct = new TextBlock { Text = $"{Math.Round(value * 100)} %", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        pct.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = value, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { pct.Text = $"{Math.Round(e.NewValue * 100)} %"; set(e.NewValue); };
        Grid.SetRow(text, row); Grid.SetRow(slider, row); Grid.SetRow(pct, row);
        Grid.SetColumn(slider, 1); Grid.SetColumn(pct, 2);
        g.Children.Add(text); g.Children.Add(slider); g.Children.Add(pct);
        row++;
    }

    // ===================== Forme et texte =====================

    private void BuildFonts()
    {
        FontChips.Children.Clear();
        var fonts = CuratedFonts.Contains(T.Font) ? CuratedFonts : [.. CuratedFonts, T.Font];
        foreach (var f in fonts) FontChips.Children.Add(FontChip(f));
    }

    private RadioButton FontChip(string f)
    {
        var rb = new RadioButton { Content = f, GroupName = "font", IsChecked = f == T.Font, FontFamily = new FontFamily(f) };
        rb.SetResourceReference(StyleProperty, "Chip");
        rb.Checked += (_, _) => { if (_loading) return; T.Font = f; ScheduleApply(); };
        return rb;
    }

    private void FontSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        FontResults.Children.Clear();
        var q = FontSearch.Text.Trim();
        if (q.Length < 2) return;
        _systemFonts ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n).ToList();
        foreach (var f in _systemFonts.Where(n => n.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(10))
        {
            var b = new Button { Content = f, FontFamily = new FontFamily(f), Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) =>
            {
                T.Font = f;
                ScheduleApply();
                FontSearch.Text = "";
                _loading = true;
                BuildFonts();
                _loading = false;
            };
            FontResults.Children.Add(b);
        }
    }

    private void Shape_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        T.Radius = Math.Round(RadiusSlider.Value);
        T.BorderWidth = Math.Round(BorderSlider.Value * 2) / 2;
        UpdateShapeTexts();
        // La taille de l'interface n'est appliquée qu'au lâcher : sinon le curseur bougerait sous la souris.
        if (sender == ScaleSlider && _scaleDragging) return;
        T.UiScale = Math.Round(ScaleSlider.Value, 2);
        ScheduleApply();
    }

    private void Scale_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _scaleDragging = false;
        T.UiScale = Math.Round(ScaleSlider.Value, 2);
        ScheduleApply();
    }

    private void UpdateShapeTexts()
    {
        ScaleText.Text = $"{Math.Round(ScaleSlider.Value * 100)} %";
        RadiusText.Text = $"{Math.Round(RadiusSlider.Value)} px";
        BorderText.Text = $"{Math.Round(BorderSlider.Value * 2) / 2} px";
    }
}
