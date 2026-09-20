using System.Globalization;
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
        TaskList.ItemsSource = null;
        TaskList.ItemsSource = tasks;
        EmptyText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyView();

        var title = _selected.ToString("dddd d MMMM", Fr);
        DayTitle.Text = char.ToUpper(title[0]) + title[1..];
        var diff = _selected.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
        var work = tasks.Where(t => !t.IsBreak).Aggregate(TimeSpan.Zero, (a, t) => a + Rhythm.WorkTime(t));
        DayLabel.Text = (diff switch { 0 => L.T("plan.day.today"), 1 => L.T("plan.day.tomorrow"), -1 => L.T("plan.day.yesterday"), > 0 => L.F("plan.day.inDays", diff), _ => L.F("plan.day.daysAgo", -diff) })
                        + (tasks.Count > 0 ? L.F("plan.day.summary", tasks.Count, PlanTask.FormatDuration(work).ToUpper(Fr)) : "");
    }

    // ===================== Jour / Semaine / Tableau =====================

    private string View => Storage.Settings.PlanView is "week" or "board" ? Storage.Settings.PlanView : "day";

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string key }) return;
        Storage.Settings.PlanView = key;
        Storage.SaveSettings();
        CloseEditors();
        Refresh();
    }

    /// <summary>Montre la vue choisie et construit ce qu'il faut. Les panneaux d'édition restent au-dessus, quelle que soit la vue.</summary>
    private void ApplyView()
    {
        var view = View;
        ViewDay.IsChecked = view == "day";
        ViewWeek.IsChecked = view == "week";
        ViewBoard.IsChecked = view == "board";

        bool editing = Editor.Visibility == Visibility.Visible
                    || PastePanel.Visibility == Visibility.Visible;

        ListScroll.Visibility = view == "day" && !editing ? Visibility.Visible : Visibility.Collapsed;
        WeekPane.Visibility = view == "week" && !editing ? Visibility.Visible : Visibility.Collapsed;
        BoardPane.Visibility = view == "board" && !editing ? Visibility.Visible : Visibility.Collapsed;

        if (WeekPane.Visibility == Visibility.Visible) BuildWeek();
        if (BoardPane.Visibility == Visibility.Visible) BuildBoard();
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
            // Déposer une tâche sur un jour du calendrier : la manière la plus rapide de dire « pas aujourd'hui ».
            cell.AllowDrop = true;
            cell.DragOver += (_, e) =>
            {
                e.Effects = TaskDrag.From(e.Data) is null ? DragDropEffects.None : DragDropEffects.Move;
                if (e.Effects != DragDropEffects.None) cell.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                e.Handled = true;
            };
            cell.DragLeave += (_, _) => { if (!isSel) cell.BorderBrush = Brushes.Transparent; };
            cell.Drop += (_, e) =>
            {
                if (!isSel) cell.BorderBrush = Brushes.Transparent;
                if (TaskDrag.From(e.Data) is not { } moved) return;
                e.Handled = true;
                TaskDrag.MoveToDay(moved, d);
                Refresh();
            };
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

    private void Press(object sender, MouseButtonEventArgs e)
    {
        Click.Down(sender);
        _dragFrom = e.GetPosition(this);
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (!Click.Up(sender) || sender is not FrameworkElement { Tag: PlanTask t }) return;
        // Un rendez-vous iCloud ne s'édite pas ici : il se modifie dans le Calendrier d'Apple, et redescend ensuite.
        if (t.IsExternal) { Undo.Note(L.T("cal.external.readOnly")); return; }
        OpenEditor(t, isNew: false);
    }

    // ===================== Faire glisser une tâche =====================

    private Point _dragFrom;
    private bool _dragging;

    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement { Tag: PlanTask t } || t.IsExternal) return;
        if (!TaskDrag.Far(_dragFrom, e.GetPosition(this))) return;

        _dragging = true;
        Click.Up(sender); // le glissement annule le clic : relâcher ne doit pas ouvrir l'éditeur
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(TaskDrag.Format, t), DragDropEffects.Move); }
        finally { _dragging = false; }
    }

    /// <summary>Survol d'une ligne pendant un glissement : un trait montre où la tâche va se poser.</summary>
    private void Row_DragOver(object sender, DragEventArgs e)
    {
        var moved = TaskDrag.From(e.Data);
        if (moved is null || sender is not Border { Tag: PlanTask target } row || ReferenceEquals(moved, target) || target.IsExternal)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        bool after = e.GetPosition(row).Y > row.ActualHeight / 2;
        row.BorderThickness = new Thickness(0, after ? 0 : 2, 0, after ? 2 : 0);
        row.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Row_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border row) { row.ClearValue(Border.BorderThicknessProperty); row.ClearValue(Border.BorderBrushProperty); }
    }

    private void Row_Drop(object sender, DragEventArgs e)
    {
        Row_DragLeave(sender, e);
        if (TaskDrag.From(e.Data) is not { } moved || sender is not Border { Tag: PlanTask target } row || target.IsExternal) return;
        e.Handled = true;
        if (moved.Date != target.Date || moved.Floating)
        {
            // Venue d'un autre jour ou de « à reprendre » : elle prend le créneau juste après celle qu'on a visée.
            TaskDrag.MoveToSlot(moved, target.Date, e.GetPosition(row).Y > row.ActualHeight / 2 ? target.End : target.Start);
        }
        else TaskDrag.Reorder(target.Date, moved, target, after: e.GetPosition(row).Y > row.ActualHeight / 2);
        Refresh();
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
        EdPinned.IsChecked = t.Pinned;
        BuildCategoryChips(EdCategories, "ed", t.CategoryId);
        BuildEmojiChips(t.Emoji);
        BuildReminderChips(t);
        _editSteps = [.. t.Steps.Select(s => new SubTask { Id = s.Id, Title = s.Title, Done = s.Done })];
        EdSteps.ItemsSource = _editSteps;
        StepBox.Text = "";
        _editLinks = t.Links.Select(l => new TaskLink { Title = l.Title, Target = l.Target }).ToList();
        EdAutoOpen.IsChecked = t.AutoOpen;
        LinkBox.Text = "";
        BuildRhythmChips(t.Rhythm);
        BuildLinkChips();
        Editor.Visibility = Visibility.Visible;
        ApplyView();
        EdTitle.Focus();
    }

    // ===================== Pictogramme, importance, rappels, étapes =====================

    /// <summary>Une poignée de pictogrammes qui couvrent l'essentiel d'une journée. La liste courte est un choix : trop d'icônes, et choisir devient la tâche.</summary>
    private static readonly string[] Emojis = ["💼", "📚", "🏃", "🧹", "🍳", "💻", "🎨", "📞", "💊", "🛒", "💬", "🌱"];

    private void BuildEmojiChips(string? selected)
    {
        EdEmojis.Children.Clear();
        Add(L.T("plan.ed.emoji.none"), null);
        foreach (var e in Emojis) Add(e, e);

        void Add(string text, string? value)
        {
            var rb = new RadioButton
            {
                Content = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = value is null ? 12 : 15 },
                Tag = value, GroupName = "edEmoji", IsChecked = value == selected,
                Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(0)
            };
            rb.SetResourceReference(StyleProperty, "Chip");
            EdEmojis.Children.Add(rb);
        }
    }

    private void BuildReminderChips(PlanTask t)
    {
        EdReminders.Children.Clear();
        // Une tâche sans choix propre hérite des rappels par défaut : cochés, mais pas encore les siens.
        var active = Reminders.For(t).ToHashSet();
        foreach (var m in Services.Reminders.Choices)
        {
            var cb = new CheckBox
            {
                Content = ReminderLabel(m), Tag = m, IsChecked = active.Contains(m), Padding = new Thickness(0)
            };
            cb.SetResourceReference(StyleProperty, "ChipCheck");
            EdReminders.Children.Add(cb);
        }
    }

    public static string ReminderLabel(int minutes) =>
        minutes == 0 ? L.T("remind.onTime") : L.F("remind.before", PlanTask.FormatDuration(TimeSpan.FromMinutes(minutes)));

    private List<SubTask> _editSteps = [];

    private void RefreshSteps()
    {
        EdSteps.ItemsSource = null;
        EdSteps.ItemsSource = _editSteps;
    }

    private void StepAdd_Click(object sender, RoutedEventArgs e) => AddStep(StepBox.Text);

    private void AddStep(string title)
    {
        title = title.Trim();
        if (title.Length == 0) return;
        _editSteps.Add(new SubTask { Title = title });
        StepBox.Text = "";
        RefreshSteps();
        StepBox.Focus();
    }

    private void StepBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // Entrée ajoute l'étape, elle n'enregistre pas la tâche
        AddStep(StepBox.Text);
    }

    private void EdStepBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; StepBox.Focus(); }
    }

    private void EdStep_Click(object sender, RoutedEventArgs e) { /* l'état est déjà lié, rien à faire avant l'enregistrement */ }

    private void StepDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SubTask s }) { _editSteps.Remove(s); RefreshSteps(); }
    }

    /// <summary>Découpe les notes en étapes : une ligne = une étape. Le plus souvent, le découpage est déjà écrit là.</summary>
    private void StepsFromNotes_Click(object sender, RoutedEventArgs e)
    {
        var lines = EdNotes.Text.Replace("\r", "").Split('\n')
            .Select(l => l.TrimStart(' ', '\t', '-', '*', '•', '·', '>').Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return;
        foreach (var line in lines.Where(l => !_editSteps.Any(s => s.Title == l)))
            _editSteps.Add(new SubTask { Title = line });
        RefreshSteps();
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
        _editing.Pinned = EdPinned.IsChecked == true;
        _editing.Emoji = EdEmojis.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
        _editing.Reminders = [.. EdReminders.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (int)c.Tag!).OrderByDescending(m => m)];
        if (StepBox.Text.Trim().Length > 0) AddStep(StepBox.Text); // étape tapée mais pas encore « ajoutée »
        _editing.Steps = [.. _editSteps.Where(s => !string.IsNullOrWhiteSpace(s.Title))];
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
        _duplicateMode = false;
        _editing = null;
        ApplyView();
    }

    // ===================== Coller un programme =====================

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        CloseEditors();
        PastePanel.Visibility = Visibility.Visible;
        ApplyView();
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
        if (replace) Storage.Data.Tasks.RemoveAll(t => t.Date == _selected && !t.IsExternal);
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
        var source = Storage.TasksFor(_selected).Where(t => !t.Floating && !t.IsExternal).ToList();
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
                Storage.Data.Tasks.RemoveAll(t => t.Date == _selected && !t.IsExternal);
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
        IcsService.Export(dlg.FileName, Storage.Data.Tasks.Where(t => t.Date >= from && !t.IsExternal));
    }

    private void CalendarSettings_Click(object sender, RoutedEventArgs e) => App.Current.MainWin?.Navigate("settings");

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
