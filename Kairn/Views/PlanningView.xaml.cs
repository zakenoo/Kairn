using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

public partial class PlanningView : UserControl, IRefreshable
{
    private static CultureInfo Fr => L.Culture; // culture de la langue choisie (noms des jours et des mois)

    /// <summary>« 9h30 » en français, « 9:30 » ailleurs.</summary>
    private static string TimeText(TimeSpan t) => Loc.Instance.Current.Code == "fr" ? PlanTask.Fmt(t).Replace(':', 'h') : PlanTask.Fmt(t);
    private DateOnly _selected = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly _month;
    private PlanTask? _editing;
    private bool _isNew;
    private bool _duplicateMode;

    public PlanningView()
    {
        InitializeComponent();
        _month = new DateOnly(_selected.Year, _selected.Month, 1);
    }

    public void SelectDate(DateOnly d)
    {
        _selected = d;
        _month = new DateOnly(d.Year, d.Month, 1);
        CloseEditors();
        Refresh();
    }

    public void Refresh()
    {
        BuildMonth();
        var tasks = Storage.TasksFor(_selected).ToList();
        TaskList.ItemsSource = tasks;
        EmptyText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var title = _selected.ToString("dddd d MMMM", Fr);
        DayTitle.Text = char.ToUpper(title[0]) + title[1..];
        var diff = _selected.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
        var work = tasks.Where(t => !t.IsBreak).Aggregate(TimeSpan.Zero, (a, t) => a + Rhythm.WorkTime(t));
        DayLabel.Text = (diff switch { 0 => L.T("plan.day.today"), 1 => L.T("plan.day.tomorrow"), -1 => L.T("plan.day.yesterday"), > 0 => L.F("plan.day.inDays", diff), _ => L.F("plan.day.daysAgo", -diff) })
                        + (tasks.Count > 0 ? L.F("plan.day.summary", tasks.Count, PlanTask.FormatDuration(work).ToUpper(Fr)) : "");
    }

    // ===================== Calendrier =====================

    private void BuildMonth()
    {
        // Initiales des jours dans la langue choisie, en commençant par lundi.
        for (int i = 0; i < 7 && i < DayNames.Children.Count; i++)
            if (DayNames.Children[i] is TextBlock tb) tb.Text = Fr.DateTimeFormat.ShortestDayNames[(i + 1) % 7].ToUpper(Fr);
        var t = _month.ToString("MMMM yyyy", Fr);
        MonthTitle.Text = char.ToUpper(t[0]) + t[1..];
        DaysGrid.Children.Clear();

        var today = DateOnly.FromDateTime(DateTime.Now);
        int offset = ((int)_month.DayOfWeek + 6) % 7; // lundi = 0
        var first = _month.AddDays(-offset);
        var counts = Storage.Data.Tasks
            .Where(x => x.Date >= first && x.Date < first.AddDays(42))
            .GroupBy(x => x.Date)
            .ToDictionary(g => g.Key, g => (total: g.Count(), done: g.Count(x => x.Done)));

        for (int i = 0; i < 42; i++)
        {
            var d = first.AddDays(i);
            bool inMonth = d.Month == _month.Month;
            bool isSel = d == _selected, isToday = d == today;

            var number = new TextBlock
            {
                Text = d.Day.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 13,
                FontWeight = isToday || isSel ? FontWeights.SemiBold : FontWeights.Normal,
            };
            number.SetResourceReference(TextBlock.ForegroundProperty,
                isToday ? "AccentBrush" : inMonth ? "TextBrush" : "FaintBrush");

            var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 3, 0, 0) };
            if (counts.TryGetValue(d, out var c))
                dot.SetResourceReference(Border.BackgroundProperty, c.done == c.total ? "BreakBrush" : "AccentBrush");

            var cell = new Border
            {
                Height = 38,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1.5),
                Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { number, dot } },
                ToolTip = counts.TryGetValue(d, out var cc) ? L.F("plan.cell.tip", cc.done, cc.total) : null
            };
            if (isSel) cell.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); else cell.Background = Brushes.Transparent;
            if (isSel) cell.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); else cell.BorderBrush = Brushes.Transparent;
            if (!isSel)
            {
                cell.MouseEnter += (_, _) => cell.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
            }
            Click.Attach(cell, () => DayClicked(d));
            DaysGrid.Children.Add(cell);
        }
    }

    private void DayClicked(DateOnly d)
    {
        if (_duplicateMode) { DuplicateTo([d]); return; }
        _selected = d;
        if (d.Month != _month.Month) _month = new DateOnly(d.Year, d.Month, 1);
        CloseEditors();
        Refresh();
    }

    private void PrevMonth_Click(object sender, RoutedEventArgs e) { _month = _month.AddMonths(-1); BuildMonth(); }
    private void NextMonth_Click(object sender, RoutedEventArgs e) { _month = _month.AddMonths(1); BuildMonth(); }
    private void Today_Click(object sender, RoutedEventArgs e) => SelectDate(DateOnly.FromDateTime(DateTime.Now));

    // ===================== Édition d'une tâche =====================

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var last = Storage.TasksFor(_selected).LastOrDefault(t => !t.Floating);
        var start = last?.End ?? new TimeSpan(Math.Max(DateTime.Now.Hour + 1, 8) % 24, 0, 0);
        OpenEditor(new PlanTask { Date = _selected, Start = start, End = start + TimeSpan.FromHours(1), CategoryId = last?.CategoryId }, isNew: true);
    }

    private void Press(object sender, MouseButtonEventArgs e) => Click.Down(sender);

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (Click.Up(sender) && sender is FrameworkElement { Tag: PlanTask t }) OpenEditor(t, isNew: false);
    }

    /// <summary>
    /// Ouvre l'édition d'une tâche existante. <paramref name="schedule"/> : on vient de « Planifier »
    /// une tâche à reprendre, donc on lui propose directement un horaire.
    /// </summary>
    public void EditTask(PlanTask t, bool schedule)
    {
        OpenEditor(t, isNew: false);
        if (schedule && t.Floating)
        {
            EdFloating.IsChecked = false;
            // Propose le premier créneau libre après la dernière tâche du jour (ou l'heure qui vient).
            var last = Storage.TasksFor(t.Date).LastOrDefault(x => !x.Floating);
            var now = DateTime.Now;
            var start = last?.End ?? new TimeSpan((now.Hour + 1) % 24, 0, 0);
            if (t.Date == DateOnly.FromDateTime(now) && start < now.TimeOfDay) start = new TimeSpan((now.Hour + 1) % 24, 0, 0);
            EdStart.Text = TimeText(start);
            EdEnd.Text = TimeText(start + t.Duration);
            EdStart.Focus();
        }
    }

    private void OpenEditor(PlanTask t, bool isNew)
    {
        CloseEditors();
        _editing = t;
        _isNew = isNew;
        EdStart.Text = TimeText(t.Start);
        EdEnd.Text = TimeText(t.End);
        EdTitle.Text = t.Title;
        EdNotes.Text = t.Notes;
        EdBreak.IsChecked = t.IsBreak;
        EdFloating.IsChecked = t.Floating;
        EdDelete.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        EdError.Visibility = Visibility.Collapsed;
        BuildCategoryChips(EdCategories, "ed", t.CategoryId);
        _editLinks = t.Links.Select(l => new TaskLink { Title = l.Title, Target = l.Target }).ToList();
        EdAutoOpen.IsChecked = t.AutoOpen;
        LinkBox.Text = "";
        BuildRhythmChips(t.Rhythm);
        BuildLinkChips();
        Editor.Visibility = Visibility.Visible;
        EdTitle.Focus();
    }

    // ===================== Liens de la tâche =====================

    private List<TaskLink> _editLinks = [];

    private void BuildLinkChips()
    {
        EdLinks.Children.Clear();
        foreach (var link in _editLinks)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(LinkIcon(link.Target, 14));
            content.Children.Add(new TextBlock { Text = link.Title, Margin = new Thickness(7, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
            var x = new TextBlock { Text = "", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
            x.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            content.Children.Add(x);
            var chip = new Button { Content = content, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 6, 10, 6), ToolTip = link.Target + "\n" + L.T("plan.links.remove") };
            chip.Click += (_, _) => { _editLinks.Remove(link); BuildLinkChips(); };
            EdLinks.Children.Add(chip);
        }
    }

    /// <summary>Icône d'une cible (vraie icône pour un programme, pictogramme pour un site).</summary>
    public static FrameworkElement LinkIcon(string target, double size)
    {
        if (Launcher.Icon(target) is { } img) return new Image { Source = img, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new TextBlock { Text = Launcher.Glyph(target), FontSize = size * 0.85, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return glyph;
    }

    private void AddLink(string target, string? title = null)
    {
        target = Launcher.Normalize(target);
        if (target.Length == 0 || _editLinks.Any(l => l.Target == target)) return;
        _editLinks.Add(new TaskLink { Title = title ?? Launcher.NameFor(target), Target = target });
        BuildLinkChips();
    }

    private void LinkAdd_Click(object sender, RoutedEventArgs e)
    {
        if (LinkBox.Text.Trim().Length == 0) return;
        AddLink(LinkBox.Text);
        LinkBox.Text = "";
    }

    private void LinkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // pas d'« Enregistrer » par défaut pendant qu'on colle un lien
        LinkAdd_Click(sender, e);
    }

    private void LinkFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = L.T("dlg.allFiles") + "|*.*|" + L.T("dlg.programs") + "|*.exe;*.lnk" };
        if (dlg.ShowDialog() == true) AddLink(dlg.FileName);
    }

    private void LinkTool_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LinkToolBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (Storage.Settings.Tools.Count == 0)
            menu.Items.Add(new MenuItem { Header = L.T("plan.tools.none"), IsEnabled = false });
        foreach (var tool in Storage.Settings.Tools)
        {
            var item = new MenuItem { Header = tool.Name, Icon = LinkIcon(tool.Target, 16) };
            item.Click += (_, _) => AddLink(tool.Target, tool.Name);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        if (!PlanParser.TryParseTime(EdStart.Text, out var start) || !PlanParser.TryParseTime(EdEnd.Text, out var end))
        {
            ShowError(L.T("plan.err.time"));
            return;
        }
        if (string.IsNullOrWhiteSpace(EdTitle.Text))
        {
            ShowError(L.T("plan.err.title"));
            return;
        }
        if (!TryReadRhythm(out var rhythm))
        {
            ShowError(L.T("rhythm.custom.invalid"));
            return;
        }
        _editing.Start = start;
        _editing.End = end;
        _editing.Title = EdTitle.Text.Trim();
        _editing.Notes = EdNotes.Text.Trim();
        _editing.IsBreak = EdBreak.IsChecked == true;
        _editing.Floating = EdFloating.IsChecked == true;
        _editing.CategoryId = SelectedCategory(EdCategories);
        if (LinkBox.Text.Trim().Length > 0) AddLink(LinkBox.Text); // lien collé mais pas encore « ajouté »
        _editing.Links = _editLinks;
        _editing.AutoOpen = EdAutoOpen.IsChecked == true && _editLinks.Count > 0;
        _editing.Rhythm = rhythm;
        if (_isNew) Storage.Data.Tasks.Add(_editing);
        Storage.Save();
        CloseEditors();
        Refresh();
    }

    private void ShowError(string msg)
    {
        EdError.Text = msg;
        EdError.Visibility = Visibility.Visible;
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => ConfirmTwice(EdDelete, () =>
    {
        if (_editing != null) Storage.Data.Tasks.Remove(_editing);
        Storage.Save();
        CloseEditors();
        Refresh();
    });

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseEditors();

    /// <summary>Corbeille au survol d'une ligne : supprimée tout de suite, « Annuler » pendant quelques secondes.</summary>
    private void QuickDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PlanTask t }) return;
        int index = Storage.Data.Tasks.IndexOf(t);
        if (index < 0) return;
        Storage.Data.Tasks.RemoveAt(index);
        Storage.Save();
        if (ReferenceEquals(_editing, t)) CloseEditors();
        Refresh();
        Undo.Show(L.F("undo.deleted", t.Title), () =>
        {
            Storage.Data.Tasks.Insert(Math.Min(index, Storage.Data.Tasks.Count), t);
            Storage.Save();
            Refresh();
        });
    }

    private void CloseEditors()
    {
        Editor.Visibility = Visibility.Collapsed;
        PastePanel.Visibility = Visibility.Collapsed;
        DuplicatePanel.Visibility = Visibility.Collapsed;
        ListScroll.Visibility = Visibility.Visible;
        _duplicateMode = false;
        _editing = null;
    }

    // ===================== Coller un programme =====================

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        CloseEditors();
        PastePanel.Visibility = Visibility.Visible;
        ListScroll.Visibility = Visibility.Collapsed;
        BuildCategoryChips(PasteCategories, "paste", null);
        if (string.IsNullOrWhiteSpace(PasteBox.Text) && Clipboard.ContainsText())
        {
            var clip = Clipboard.GetText();
            if (PlanParser.Parse(clip, _selected).Count > 0) PasteBox.Text = clip;
        }
        PasteBox.Focus();
        UpdatePreview();
    }

    private void PasteBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private List<PlanTask> UpdatePreview()
    {
        var tasks = PlanParser.Parse(PasteBox.Text, _selected);
        PreviewList.ItemsSource = tasks;
        PreviewCount.Text = tasks.Count == 0 ? L.T("plan.preview.none") : L.P("plan.preview.count", tasks.Count);
        return tasks;
    }

    private void PasteAdd_Click(object sender, RoutedEventArgs e) => ApplyPaste(replace: false);
    private void PasteReplace_Click(object sender, RoutedEventArgs e) => ApplyPaste(replace: true);
    private void PasteCancel_Click(object sender, RoutedEventArgs e) => CloseEditors();

    private void ApplyPaste(bool replace)
    {
        var tasks = UpdatePreview();
        if (tasks.Count == 0) return;
        var cat = SelectedCategory(PasteCategories);
        if (replace) Storage.Data.Tasks.RemoveAll(t => t.Date == _selected);
        foreach (var t in tasks)
        {
            t.CategoryId = t.IsBreak ? null : cat;
            Storage.Data.Tasks.Add(t);
        }
        Storage.Save();
        PasteBox.Text = "";
        CloseEditors();
        Refresh();
    }

    // ===================== Dupliquer / vider =====================

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (!Storage.TasksFor(_selected).Any()) return;
        CloseEditors();
        _duplicateMode = true;
        DuplicatePanel.Visibility = Visibility.Visible;
        DuplicateTargets.Children.Clear();
        AddTarget(L.T("plan.dup.tomorrow"), [_selected.AddDays(1)]);
        AddTarget(L.T("plan.dup.next6"), Enumerable.Range(1, 6).Select(_selected.AddDays).ToArray());
        AddTarget(L.T("plan.dup.nextWeek"), [_selected.AddDays(7)]);
        var hint = new TextBlock { Text = L.T("plan.dup.hint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 8) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
        DuplicateTargets.Children.Add(hint);

        void AddTarget(string label, DateOnly[] dates)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => DuplicateTo(dates);
            DuplicateTargets.Children.Add(b);
        }
    }

    private void DuplicateTo(DateOnly[] dates)
    {
        var source = Storage.TasksFor(_selected).Where(t => !t.Floating).ToList();
        foreach (var d in dates.Where(d => d != _selected))
            Storage.Data.Tasks.AddRange(source.Select(t => t.Clone(d)));
        Storage.Save();
        CloseEditors();
        Refresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && Storage.TasksFor(_selected).Any())
            ConfirmTwice(b, () =>
            {
                Storage.Data.Tasks.RemoveAll(t => t.Date == _selected);
                Storage.Save();
                CloseEditors();
                Refresh();
            });
    }

    // ===================== .ics =====================

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = L.T("dlg.calendar") + " (*.ics)|*.ics", FileName = "kairn.ics" };
        if (dlg.ShowDialog() != true) return;
        // Exporte à partir d'aujourd'hui (les jours passés n'ont pas d'intérêt dans un agenda).
        var from = DateOnly.FromDateTime(DateTime.Now);
        IcsService.Export(dlg.FileName, Storage.Data.Tasks.Where(t => t.Date >= from));
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = L.T("dlg.calendar") + " (*.ics)|*.ics" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var existing = Storage.Data.Tasks.Select(t => t.Id).ToHashSet();
            var imported = IcsService.Import(dlg.FileName).Where(t => !existing.Contains(t.Id)).ToList();
            Storage.Data.Tasks.AddRange(imported);
            Storage.Save();
            Refresh();
        }
        catch { /* fichier illisible : on ignore */ }
    }

    // ===================== Utilitaires =====================

    private static void BuildCategoryChips(WrapPanel panel, string group, string? selectedId)
    {
        panel.Children.Clear();
        panel.Children.Add(Chip(L.T("plan.category.none"), null, null));
        foreach (var (c, _) in Storage.CategoryTree())
            panel.Children.Add(Chip($"{c.Emoji} {Storage.CategoryPath(c)}", c.Id, c.Color));

        RadioButton Chip(string text, string? id, string? color)
        {
            var rb = new RadioButton
            {
                Content = text, Tag = id, GroupName = group + panel.GetHashCode(),
                IsChecked = id == selectedId, Padding = new Thickness(0)
            };
            rb.SetResourceReference(StyleProperty, "Chip");
            return rb;
        }
    }

    private static string? SelectedCategory(WrapPanel panel) =>
        panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    /// <summary>Premier clic : le bouton demande confirmation. Second clic dans les 3 s : l'action est exécutée.</summary>
    public static void ConfirmTwice(Button b, Action action)
    {
        if (Armed.TryGetValue(b, out var state))
        {
            Armed.Remove(b);
            state.Restore();
            action();
            return;
        }
        var content = b.Content;
        var fg = b.ReadLocalValue(ForegroundProperty);
        var st = new ArmedState(() =>
        {
            b.Content = content;
            if (fg == DependencyProperty.UnsetValue) b.ClearValue(ForegroundProperty); else b.SetValue(ForegroundProperty, fg);
        });
        Armed.Add(b, st);
        b.Content = L.T("common.confirm");
        b.SetResourceReference(ForegroundProperty, "DangerBrush");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (Armed.TryGetValue(b, out var s) && ReferenceEquals(s, st))
            {
                Armed.Remove(b);
                st.Restore();
            }
        };
        timer.Start();
    }

    private sealed record ArmedState(Action Restore);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, ArmedState> Armed = new();
}
