using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

public partial class TodayView : UserControl, IRefreshable
{

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private List<PlanTask> _tasks = [];      // tâches avec horaire
    private List<PlanTask> _carried = [];    // tâches à reprendre (sans horaire)
    private DateOnly _day;
    private int _stonesShown = -1;

    private const int QuoteCount = 10; // clés today.quote.0 à today.quote.9

    public TodayView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        _feedbackTimer.Tick += (_, _) => { _feedbackTimer.Stop(); Feedback.Text = ""; };
        InitSide();
        InitPulse();
        // Le chrono ne tourne que si la page est vraiment à l'écran : ni fenêtre cachée, ni réduite, ni autre onglet.
        // Les données, elles, sont remises à jour par la fenêtre principale quand elles ont changé.
        IsVisibleChanged += (_, _) => UpdateActivity();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window != null) _window.StateChanged += OnWindowStateChanged;
            UpdateActivity();
        };
        Unloaded += (_, _) =>
        {
            if (_window != null) _window.StateChanged -= OnWindowStateChanged;
            _window = null;
            UpdateActivity();
        };
    }

    private Window? _window;
    private bool _active;

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateActivity();

    /// <summary>
    /// Démarre ou arrête ce qui tourne pour cette page (chrono, relève des mails, pastille).
    /// Au retour à l'écran, le compte à rebours et les mails sont remis à jour tout de suite.
    /// </summary>
    private void UpdateActivity()
    {
        bool active = IsVisible && _window is { WindowState: not WindowState.Minimized };
        if (active != _active)
        {
            _active = active;
            if (active)
            {
                _timer.Start();
                _mailTimer.Start();
                Tick();
                FetchMail(force: false);
            }
            else
            {
                _timer.Stop();
                _mailTimer.Stop();
            }
        }
        UpdatePulse();
    }

    public void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today != _day && _day != default) CarryOver.RollOver(); // minuit passé avec l'app ouverte
        _day = today;

        var all = Storage.TasksFor(_day).ToList();
        _tasks = all.Where(t => !t.Floating).ToList();
        _carried = all.Where(t => t.Floating).ToList();

        var view = new ListCollectionView(_tasks);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PlanTask.Section)));
        TaskList.ItemsSource = view;

        CarryList.ItemsSource = _carried;
        CarryCard.Visibility = _carried.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CarryTitle.Text = _carried.Count > 1 ? L.F("today.carry.count", _carried.Count) : L.T("today.carry.title");

        // L'essentiel : ce qui a été épinglé, au maximum trois. Au-delà, ce n'est plus l'essentiel.
        var essential = all.Where(t => t.Pinned && !t.IsBreak).Take(3).ToList();
        EssentialList.ItemsSource = essential;
        EssentialCard.Visibility = essential.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        FocusToggle.IsChecked = Storage.Settings.FocusOnly;

        var hour = DateTime.Now.Hour;
        var name = string.IsNullOrWhiteSpace(Storage.Settings.UserName) ? "" : " " + Storage.Settings.UserName;
        Greeting.Text = L.T(hour < 5 ? "today.greet.night" : hour < 12 ? "today.greet.morning" : hour < 18 ? "today.greet.afternoon" : "today.greet.evening") + name + ".";
        DateText.Text = DateTime.Now.ToString("dddd d MMMM", L.Culture).ToUpper(L.Culture);
        Quote.Text = L.F("today.quoteFormat", L.T($"today.quote.{_day.DayNumber % QuoteCount}"));

        bool empty = _tasks.Count == 0;
        EmptyCard.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        _shown = null;
        RefreshSide();
        UpdateStats();
        Tick();
        ApplyFocus();
    }

    // ===================== Mode focus =====================

    /// <summary>
    /// « Ne me montre que ce que je fais maintenant. » Tout le reste est masqué, pas supprimé :
    /// une liste de quinze lignes suffit à paralyser, et l'essentiel n'en est qu'une.
    /// </summary>
    private void ApplyFocus()
    {
        bool focus = Storage.Settings.FocusOnly;
        bool empty = _tasks.Count == 0;

        Quote.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
        ListHeader.Visibility = empty || focus ? Visibility.Collapsed : Visibility.Visible;
        TaskList.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
        CarryCard.Visibility = !focus && _carried.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // L'essentiel reste visible même en focus : c'est justement ce qu'on a décidé de ne pas perdre de vue.
        LayoutSide(Layout.ActualWidth);
        UpdateEarlier(DateTime.Now.TimeOfDay);
    }

    // ===================== Prévu plus tôt =====================

    private string? _earlierKey;
    private bool _earlierExpanded;

    /// <summary>
    /// Ce dont l'heure est passée sans que ce soit fait : la journée a glissé (réveil tardif, imprévu), et on le voit au lieu de le deviner.
    /// Une ou deux tâches se montrent une par une ; au-delà, une seule ligne qui ouvre la liste, pour ne pas faire un mur.
    /// Rien n'est compté ni reproché : elles quittent la carte dès qu'on les fait, les décale ou les remet à plus tard.
    /// </summary>
    private void UpdateEarlier(TimeSpan now)
    {
        // Un rendez-vous iCloud ne se coche pas, et une tâche qui passe minuit n'a pas fini son créneau.
        var earlier = _tasks.Where(x => !x.Done && !x.IsBreak && !x.IsExternal && x.End > x.Start && x.End <= now).ToList();
        bool show = earlier.Count > 0 && !Storage.Settings.FocusOnly;
        EarlierCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        bool many = earlier.Count > 2;
        EarlierSummary.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        EarlierList.Visibility = !many || _earlierExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (many)
        {
            EarlierSummaryText.Text = L.P("today.earlier.summary", earlier.Count);
            EarlierToggle.Content = L.T(_earlierExpanded ? "today.earlier.hide" : "today.earlier.show");
        }

        // La liste n'est refaite que si elle a changé : cette méthode passe chaque seconde.
        var key = string.Join(',', earlier.Select(x => x.Id));
        if (key == _earlierKey) return;
        _earlierKey = key;
        EarlierList.ItemsSource = earlier;
    }

    private void EarlierToggle_Click(object sender, RoutedEventArgs e)
    {
        _earlierExpanded = !_earlierExpanded;
        UpdateEarlier(DateTime.Now.TimeOfDay);
    }

    private void EarlierStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PlanTask t }) StartNow(t);
    }

    private void EarlierLater_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PlanTask t }) Postponed(CarryOver.Postpone(t), 1);
    }

    private void FocusToggle_Click(object sender, RoutedEventArgs e)
    {
        Storage.Settings.FocusOnly = FocusToggle.IsChecked == true;
        Storage.SaveSettings();
        ApplyFocus();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        if (DateOnly.FromDateTime(now) != _day) { Refresh(); return; }
        if (FocusGuard.CurrentTask()?.Id != _mailBlockId) RefreshMail(); // début ou fin de bloc : les mails se masquent ou réapparaissent
        var t = now.TimeOfDay;

        PlanTask? current = null;
        foreach (var task in _tasks)
        {
            bool inSlot = task.Start <= t && (task.End > t || task.End <= task.Start);
            task.IsPast = !inSlot && task.End <= t && task.End > task.Start;
            // Deux tâches sur le même créneau (une finie en avance, puis la suivante avancée) : c'est celle qui reste à faire qui compte.
            if (inSlot && (current is null || (current.Done && !task.Done))) current = task;
        }
        foreach (var task in _tasks) task.IsCurrent = ReferenceEquals(task, current);
        UpdateEarlier(t);
        var next = _tasks.FirstOrDefault(x => x.Start > t);

        if (_tasks.Count == 0) { NowCard.Visibility = Visibility.Collapsed; UpdatePulse(); return; }
        NowCard.Visibility = Visibility.Visible;
        UpdatePulse();

        if (current != null)
        {
            var end = current.End > current.Start ? current.End : current.End + TimeSpan.FromDays(1);
            var nextText = next != null ? L.F("today.nextTask", PlanTask.Fmt(next.Start), next.Title) : L.T("today.lastBlock");
            var progress = (t - current.Start).TotalSeconds / Math.Max(1, current.Duration.TotalSeconds);
            if (current.Done)
            {
                // Finie en avance : le compte à rebours continue (un clic de travers se rattrape en décochant),
                // mais la carte le dit, et propose de commencer tout de suite la tâche suivante.
                // Un rendez-vous iCloud ne se déplace pas : pas de proposition s'il vient ensuite.
                var upcoming = _tasks.FirstOrDefault(x => x.Start > t && !x.Done);
                ShowCard(current, L.T("today.label.done"), Clock(end - t), L.T("today.remaining"), progress, isNow: true,
                         advance: upcoming is { IsExternal: false } ? upcoming : null);
                NextText.Text = nextText;
            }
            else if (!ShowRhythm(current, now, nextText))
            {
                ShowCard(current, L.T(current.IsBreak ? "today.label.break" : "today.label.now"), Clock(end - t), L.T("today.remaining"),
                         progress, isNow: true);
                NextText.Text = nextText;
            }
        }
        else if (next != null)
        {
            ShowCard(next, L.T("today.label.next"), Clock(next.Start - t), L.T("today.beforeStart"), 0, isNow: false);
            NextText.Text = L.T("today.prepare");
        }
        else
        {
            ShowDayOver();
        }
    }

    private PlanTask? _shown;

    /// <param name="advance">Tâche suivante qu'on peut commencer tout de suite (tâche en cours finie en avance), sinon null.</param>
    private void ShowCard(PlanTask task, string label, string remaining, string remainingLabel, double progress, bool isNow,
                          bool rhythm = false, PlanTask? advance = null)
    {
        NowLabel.Text = label;
        // Tâche finie : la pastille ne respire plus, rien n'est « en direct ».
        _calm = isNow && task.Done;
        UpdatePulse();
        NowNext.Visibility = advance != null ? Visibility.Visible : Visibility.Collapsed;
        NowNext.Tag = advance;
        NowNext.ToolTip = advance is null ? null : L.F("today.nextNow.tip", advance.Title);
        NowRange.Text = $"{task.TimeRange} · {task.DurationText}";
        if (!rhythm) RhythmBar.Visibility = Visibility.Collapsed;
        Remaining.Text = remaining;
        RemainingLabel.Text = remainingLabel;
        // Un rendez-vous iCloud s'affiche et se décompte, mais il ne se coche pas et ne se reporte pas.
        NowDone.Visibility = task.Done || task.IsExternal ? Visibility.Collapsed : Visibility.Visible;
        NowDone.Tag = task;
        bool canPostpone = !task.Done && !task.IsBreak && !task.IsExternal && isNow;
        NowPostpone.Visibility = canPostpone ? Visibility.Visible : Visibility.Collapsed;
        NowPostpone.Tag = task;
        NowPostponeText.Text = L.T("today.postpone");
        if (NowBar.Parent is Grid g)
        {
            g.Visibility = rhythm ? Visibility.Collapsed : Visibility.Visible;
            NowBar.Width = Math.Clamp(progress, 0, 1) * g.ActualWidth;
        }

        if (!ReferenceEquals(_shown, task))
        {
            _shown = task;
            NowTitle.Text = task.Title;
            NowNotes.Text = task.Notes;
            NowNotes.Visibility = task.HasNotes ? Visibility.Visible : Visibility.Collapsed;
            // Ressources de la catégorie et de ses sous-catégories.
            var cat = Storage.CategoryById(task.CategoryId);
            var items = cat is null ? null : Storage.WithDescendants(cat).SelectMany(c => c.Items).Where(i => i.Kind != ResourceKind.Note).Take(8).ToList();
            NowResources.ItemsSource = items;
            NowResources.Visibility = items is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            ShowTaskLinks(task);
            ShowSteps(task);
        }
        else if (!ReferenceEquals(NowSteps.ItemsSource, task.Steps)) ShowSteps(task);
    }

    /// <summary>Les étapes de la tâche en cours : la liste sur laquelle on peut vraiment commencer.</summary>
    private void ShowSteps(PlanTask? task)
    {
        if (task is null || task.Steps.Count == 0)
        {
            NowSteps.ItemsSource = null;
            NowStepsBox.Visibility = Visibility.Collapsed;
            return;
        }
        NowSteps.ItemsSource = task.Steps;
        NowStepsCount.Text = task.StepsText;
        NowStepsBox.Visibility = Visibility.Visible;
    }

    /// <summary>Fin de journée : on célèbre ce qui est fait, et on propose de reporter le reste.</summary>
    private void ShowDayOver()
    {
        if (_shown is { } && _shown.Id == "__dayover") return;
        _shown = new PlanTask { Id = "__dayover" };

        int done = _tasks.Count(x => x.Done && !x.IsBreak) + _carried.Count(x => x.Done);
        int left = _tasks.Count(x => !x.Done && !x.IsBreak);
        NowLabel.Text = L.T("today.label.dayOver");
        NowRange.Text = "";
        NowTitle.Text = done == 0 ? L.T("today.dayOver.none") : L.P("today.dayOver.done", done);
        NowNotes.Text = left == 0
            ? L.T("today.dayOver.rest")
            : L.P("today.dayOver.left", left, CarryOver.DayName(CarryOver.NextSessionDate(afterNow: true)));
        NowNotes.Visibility = Visibility.Visible;
        NowResources.ItemsSource = null;
        ShowTaskLinks(null);
        NowResources.Visibility = Visibility.Collapsed;
        RhythmBar.Visibility = Visibility.Collapsed;
        Remaining.Text = "";
        RemainingLabel.Text = "";
        NowDone.Visibility = Visibility.Collapsed;
        NowNext.Visibility = Visibility.Collapsed;
        _calm = false;
        UpdatePulse();
        NowPostpone.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
        NowPostpone.Tag = "all";
        NowPostponeText.Text = L.T("today.postponeNow");
        if (NowBar.Parent is Grid bar) bar.Visibility = Visibility.Collapsed;
        NextText.Text = "";
    }

    private static string Clock(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:00}" : $"{d.Minutes:00}:{d.Seconds:00}";

    // ===================== Progression : le cairn =====================

    private void UpdateStats()
    {
        // Une pierre par tâche finie, et une par étape finie : commencer compte autant que terminer.
        int done = _tasks.Count(x => x.Done && !x.IsBreak && !x.IsExternal) + _carried.Count(x => x.Done)
                 + _tasks.Concat(_carried).Where(x => !x.IsBreak).Sum(x => x.StepsDone);
        DoneText.Text = done == 0 ? L.T("today.firstStone") : L.P("today.stones", done);

        var (path, tip) = Progress.Describe();
        PathText.Text = path;
        PathText.ToolTip = tip;

        var today = _tasks.Concat(_carried).Where(x => x.Done && !x.IsBreak).Aggregate(TimeSpan.Zero, (a, x) => a + Rhythm.WorkTime(x));
        FocusTimeText.Text = today > TimeSpan.Zero ? L.F("today.workToday", PlanTask.FormatDuration(today)) : "";

        // Cumul de la semaine : ne fait que monter, ne se « casse » jamais.
        var monday = _day.AddDays(-(((int)_day.DayOfWeek + 6) % 7));
        var week = Storage.Data.Tasks.Where(x => x.Done && !x.IsBreak && x.Date >= monday && x.Date <= _day)
                                     .Aggregate(TimeSpan.Zero, (a, x) => a + Rhythm.WorkTime(x));
        WeekText.Text = week > TimeSpan.Zero ? L.F("today.workWeek", PlanTask.FormatDuration(week)) : "";

        DrawCairn(done);
    }

    private void DrawCairn(int stones)
    {
        // Le cairn monte un peu plus haut à chaque palier du sentier : rien ne se perd, tout s'accumule.
        int maxDrawn = Progress.Current(Progress.Stones()).Cairn;
        int drawn = Math.Min(stones, maxDrawn);
        bool grew = _stonesShown >= 0 && stones > _stonesShown;
        _stonesShown = stones;
        Cairn.Children.Clear();

        if (drawn == 0)
        {
            // Emplacement vide, en pointillés : une invitation, pas un manque.
            var slot = new Ellipse { Width = 64, Height = 16, StrokeThickness = 1.5, StrokeDashArray = [3, 3] };
            slot.SetResourceReference(Shape.StrokeProperty, "FaintBrush");
            Canvas.SetLeft(slot, 10);
            Canvas.SetTop(slot, 94);
            Cairn.Children.Add(slot);
            return;
        }

        // Le tas s'aplatit pour tenir dans la hauteur disponible, même à dix-sept pierres.
        const double Base = 112, Gap = 2, Head = 18;
        double natural = 0;
        for (int i = 0; i < drawn; i++) natural += Math.Max(9, 16 - i) + Gap;
        double squeeze = Math.Min(1, (Base - Head) / natural);

        double y = Base;
        for (int i = 0; i < drawn; i++)
        {
            double w = Math.Max(22, 74 - i * (52.0 / Math.Max(1, maxDrawn - 1)));
            double h = Math.Max(9, 16 - i) * squeeze;
            y -= h + Gap * squeeze;
            double jitter = i % 3 == 0 ? 0 : i % 3 == 1 ? 3 : -3;
            var stone = new Ellipse { Width = w, Height = h, Opacity = 1 - i * 0.06 };
            stone.SetResourceReference(Shape.FillProperty, "AccentBrush");
            Canvas.SetLeft(stone, (84 - w) / 2 + jitter);
            Canvas.SetTop(stone, y);
            Cairn.Children.Add(stone);

            if (grew && i == drawn - 1)
            {
                // La nouvelle pierre se pose en douceur.
                var tt = new TranslateTransform(0, -14);
                stone.RenderTransform = tt;
                stone.BeginAnimation(OpacityProperty, new DoubleAnimation(0, stone.Opacity, TimeSpan.FromMilliseconds(350)));
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(420))
                    { EasingFunction = new BounceEase { Bounces = 1, Bounciness = 4, EasingMode = EasingMode.EaseOut } });
            }
        }

        if (stones > maxDrawn)
        {
            var more = new TextBlock { Text = $"+{stones - maxDrawn}", FontSize = 11, FontWeight = FontWeights.SemiBold };
            more.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            Canvas.SetLeft(more, 34);
            Canvas.SetTop(more, y - 16);
            Cairn.Children.Add(more);
        }
    }

    // ===================== Actions =====================

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        // La récompense arrive avant le rangement : c'est elle qu'on est venu chercher.
        if (sender is CheckBox { Tag: PlanTask task })
        {
            if (task.Done) Reward(task, sender as FrameworkElement);
            else Reminders.Cancel(task);
        }
        Storage.Save();
        UpdateStats();
        _shown = null;
        Tick();
        ApplyFocus();
    }

    /// <summary>Une étape cochée : un son plus discret, et la tâche se termine toute seule quand la dernière tombe.</summary>
    private void Step_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: SubTask step }) return;
        if (step.Done) { Celebrate.Step(); Celebrate.Flash((FrameworkElement)sender); }
        NowStepsCount.Text = _shown?.StepsText ?? "";
        Storage.Save();
        UpdateStats();
    }

    /// <summary>Le son, la petite animation, et les confettis quand c'était la dernière de la journée.</summary>
    private void Reward(PlanTask task, FrameworkElement? row)
    {
        // « La dernière de la journée » ne compte que ce qui t'appartient : un rendez-vous iCloud n'est pas une tâche à finir.
        bool lastOfDay = _tasks.Concat(_carried).All(x => x.Done || x.IsBreak || x.IsExternal);
        if (lastOfDay) { Celebrate.Day(); Celebrate.Confetti(NowCard); }
        else Celebrate.Task();
        if (row != null) Celebrate.Flash(row);
        Reminders.Cancel(task);
    }

    private void NowDone_Click(object sender, RoutedEventArgs e)
    {
        if (NowDone.Tag is PlanTask t)
        {
            t.Done = true;
            Reward(t, NowCard);
        }
        Storage.Save();
        UpdateStats();
        _shown = null;
        Tick();
        ApplyFocus();
    }

    /// <summary>
    /// Tâche finie en avance : la suivante commence maintenant, avec la même durée (et ses étapes, liens, rythme…).
    /// La tâche finie garde son créneau : rien ne se retire du temps de travail de la journée.
    /// </summary>
    private void NowNext_Click(object sender, RoutedEventArgs e)
    {
        if (NowNext.Tag is not PlanTask next || next.Done || next.IsExternal) return;
        StartNow(next);
    }

    /// <summary>La tâche commence maintenant, avec la même durée. Sert à « Commencer la suivante » et à « Je m'y mets ».</summary>
    private void StartNow(PlanTask task)
    {
        var now = DateTime.Now;
        var length = task.Duration;
        var start = new TimeSpan(now.Hour, now.Minute, 0);
        var end = start + length;
        if (end.TotalHours >= 24) end -= TimeSpan.FromDays(1); // passe minuit
        task.Start = start;
        task.End = end;
        task.CarriedFrom = null;
        // Lancée à la main à l'instant : pas de rappel « c'est l'heure » dans la foulée.
        Reminders.Mute(task, now);
        Storage.Save();
        _shown = null;
        Refresh(); // l'ordre du programme change
    }

    // ===================== L'essentiel =====================

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PlanTask t }) return;
        // Trois au maximum : la plus ancienne épingle cède sa place, sans rien demander.
        if (!t.Pinned)
        {
            var pinned = Storage.TasksFor(_day).Where(x => x.Pinned).ToList();
            if (pinned.Count >= 3) pinned[0].Pinned = false;
        }
        t.Pinned = !t.Pinned;
        Storage.Save();
        Refresh();
    }

    private void Unpin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PlanTask t }) return;
        t.Pinned = false;
        Storage.Save();
        Refresh();
    }

    private void NowPostpone_Click(object sender, RoutedEventArgs e)
    {
        if (NowPostpone.Tag is PlanTask t) Postponed(CarryOver.Postpone(t), 1);
        else if (NowPostpone.Tag as string == "all")
        {
            int n = _tasks.Count(x => !x.Done && !x.IsBreak);
            Postponed(CarryOver.PostponeRemaining(_day), n);
        }
    }

    private void PostponeRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PlanTask t }) Postponed(CarryOver.Postpone(t), 1);
    }

    private void Postponed(DateOnly target, int count)
    {
        Refresh();
        Feedback.Text = L.P("today.postponed", count, CarryOver.DayName(target));
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PlanTask t })
            App.Current.MainWin?.Navigate("planning", t.Date, t);
    }

    private void Plan_Click(object sender, RoutedEventArgs e) =>
        App.Current.MainWin?.Navigate("planning", DateOnly.FromDateTime(DateTime.Now));

    private void Resource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ResourceItem item }) LibraryView.Open(item);
    }
}
