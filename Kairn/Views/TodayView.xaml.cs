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
        // Le chrono ne tourne que si la vue est réellement affichée (pas quand l'app est dans la barre des tâches).
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(); _timer.Start(); }
            else _timer.Stop();
        };
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
            bool isCurrent = task.Start <= t && (task.End > t || task.End <= task.Start);
            task.IsCurrent = isCurrent;
            task.IsPast = !isCurrent && task.End <= t && task.End > task.Start;
            if (isCurrent && current is null) current = task;
        }
        var next = _tasks.FirstOrDefault(x => x.Start > t);

        if (_tasks.Count == 0) { NowCard.Visibility = Visibility.Collapsed; return; }
        NowCard.Visibility = Visibility.Visible;

        if (current != null)
        {
            var end = current.End > current.Start ? current.End : current.End + TimeSpan.FromDays(1);
            var nextText = next != null ? L.F("today.nextTask", PlanTask.Fmt(next.Start), next.Title) : L.T("today.lastBlock");
            if (!ShowRhythm(current, now, nextText))
            {
                ShowCard(current, L.T(current.IsBreak ? "today.label.break" : "today.label.now"), Clock(end - t), L.T("today.remaining"),
                         (t - current.Start).TotalSeconds / Math.Max(1, current.Duration.TotalSeconds), isNow: true);
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

    private void ShowCard(PlanTask task, string label, string remaining, string remainingLabel, double progress, bool isNow, bool rhythm = false)
    {
        NowLabel.Text = label;
        NowRange.Text = $"{task.TimeRange} · {task.DurationText}";
        if (!rhythm) RhythmBar.Visibility = Visibility.Collapsed;
        Remaining.Text = remaining;
        RemainingLabel.Text = remainingLabel;
        NowDone.Visibility = task.Done ? Visibility.Collapsed : Visibility.Visible;
        NowDone.Tag = task;
        bool canPostpone = !task.Done && !task.IsBreak && isNow;
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
        }
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
        int done = _tasks.Count(x => x.Done && !x.IsBreak) + _carried.Count(x => x.Done);
        DoneText.Text = done == 0 ? L.T("today.firstStone") : L.P("today.doneCount", done);

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
        const int maxDrawn = 7;
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
            Canvas.SetTop(slot, 78);
            Cairn.Children.Add(slot);
            return;
        }

        double y = 96;
        for (int i = 0; i < drawn; i++)
        {
            double w = Math.Max(22, 74 - i * 8);
            double h = Math.Max(9, 16 - i * 1);
            y -= h + 2;
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
        Storage.Save();
        UpdateStats();
        _shown = null;
        Tick();
    }

    private void NowDone_Click(object sender, RoutedEventArgs e)
    {
        if (NowDone.Tag is PlanTask t) t.Done = true;
        Check_Click(sender, e);
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
