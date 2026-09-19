using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kairn.Models;
using Kairn.Services;
using Kairn.Services.Assistant;

namespace Kairn.Views;

/// <summary>
/// Onglet Objectifs : « j'aimerais faire tel truc, j'ai tant de temps » → quelques questions → un programme
/// placé dans les créneaux libres, relu et validé avant d'entrer dans le calendrier.
/// </summary>
public partial class GoalsView : UserControl, IRefreshable
{
    private static readonly (string Key, int Days)[] Durations =
        [("goal.dur.2w", 14), ("goal.dur.1m", 30), ("goal.dur.2m", 61), ("goal.dur.3m", 91), ("goal.dur.6m", 182)];
    private static readonly int[] Lengths = [20, 30, 45, 60, 90];

    private Goal _goal = new();
    private GoalFrame? _frame;
    private List<GoalSession> _sessions = [];
    private List<PlanTask> _placed = [];
    private List<CheckBox> _keep = [];
    /// <summary>Vague suivante d'un objectif existant (sinon : nouvel objectif).</summary>
    private bool _isNextWave;
    private DateOnly _waveFrom;
    private bool _online;
    private CancellationTokenSource? _cts;
    private Func<Task>? _retry;
    private readonly DispatcherTimer _busyTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _busySince;

    public GoalsView()
    {
        InitializeComponent();
        _busyTimer.Tick += (_, _) => BusyTime.Text = L.F("goal.busy.elapsed", (int)(DateTime.Now - _busySince).TotalSeconds);
        BuildForm();
    }

    public void Refresh()
    {
        UpdateModes();
        BuildGoalList();
    }

    // ===================== Formulaire =====================

    private void BuildForm()
    {
        DurationChips.Children.Clear();
        foreach (var (key, days) in Durations)
            DurationChips.Children.Add(Radio("dur", L.T(key), days, days == 61));

        DayChips.Children.Clear();
        var culture = L.Culture;
        int first = (int)culture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            var d = (DayOfWeek)((first + i) % 7);
            var cb = new CheckBox { Content = culture.DateTimeFormat.GetAbbreviatedDayName(d), Tag = d, IsChecked = d is >= DayOfWeek.Monday and <= DayOfWeek.Friday };
            cb.SetResourceReference(StyleProperty, "ChipCheck");
            DayChips.Children.Add(cb);
        }

        WindowChips.Children.Clear();
        foreach (var w in Enum.GetValues<GoalWindow>())
            WindowChips.Children.Add(Radio("win", L.T("goal.win." + w.ToString().ToLowerInvariant()), w, w == GoalWindow.Evening));

        LengthChips.Children.Clear();
        foreach (var m in Lengths)
            LengthChips.Children.Add(Radio("len", PlanTask.FormatDuration(TimeSpan.FromMinutes(m)), m, m == 45));
    }

    private static RadioButton Radio(string group, string text, object tag, bool isChecked)
    {
        var rb = new RadioButton { Content = text, Tag = tag, GroupName = "goal." + group, IsChecked = isChecked };
        rb.SetResourceReference(StyleProperty, "Chip");
        return rb;
    }

    private static T? Picked<T>(Panel p) =>
        p.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is T v ? v : default;

    /// <summary>Local ou en ligne : on ne propose que ce qui est prêt, et on dit clairement ce qui sort du PC.</summary>
    private void UpdateModes()
    {
        bool local = GoalAssistant.LocalReady, online = GoalAssistant.OnlineReady;
        ModeChips.Children.Clear();
        if (local) ModeChips.Children.Add(Radio("mode", "🔒 " + L.T("goal.mode.local"), false, !online || !_online));
        if (online) ModeChips.Children.Add(Radio("mode", "🌐 " + L.T("goal.mode.online"), true, _online || !local));
        foreach (RadioButton rb in ModeChips.Children) rb.Checked += (_, _) => { _online = rb.Tag is true; UpdatePrivacy(); };
        _online = online && (!local || _online);
        ModePanel.Visibility = local && online ? Visibility.Visible : Visibility.Collapsed;
        NotReady.Visibility = local || online ? Visibility.Collapsed : Visibility.Visible;
        GoBtn.IsEnabled = local || online;
        UpdatePrivacy();
    }

    private void UpdatePrivacy()
    {
        var s = Storage.Settings.Assistant;
        PrivacyText.Text = !_online
            ? L.T("goal.privacy.local")
            : L.F("goal.privacy.online", s.OnlineProvider == "openai" ? L.T("set.ai.provider.openai") : "Claude (Anthropic)");
    }

    private void Setup_Click(object sender, RoutedEventArgs e) => App.Current.MainWin?.Navigate("settings");

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        FormError.Text = "";
        var request = RequestBox.Text.Trim();
        var days = DayChips.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (DayOfWeek)c.Tag).ToList();
        if (request.Length < 4) { FormError.Text = L.T("goal.err.request"); return; }
        if (days.Count == 0) { FormError.Text = L.T("goal.err.days"); return; }

        var today = DateOnly.FromDateTime(DateTime.Now);
        _goal = new Goal
        {
            Request = request,
            Start = today,
            Deadline = today.AddDays(Picked<int>(DurationChips) is > 0 and var d ? d : 61),
            Days = days,
            Window = Picked<GoalWindow>(WindowChips),
            SessionMinutes = Picked<int>(LengthChips) is > 0 and var m ? m : 45,
            Online = _online,
        };
        _isNextWave = false;
        await Run(FrameStep);
    }

    // ===================== Étapes =====================

    private async Task FrameStep()
    {
        ShowBusy(L.T("goal.busy.frame"));
        _frame = await GoalAssistant.FrameAsync(Llm(), _goal, _cts!.Token);
        _goal.Title = _frame.Title;
        if (_frame.Questions.Count == 0 && _frame.Feasibility == "ok") { await PlanStep(); return; }

        QTitle.Text = _frame.Title;
        FeasText.Text = (_frame.Feasibility switch { "tight" => "⚠️ ", "unrealistic" => "⛔ ", _ => "✓ " }) + _frame.Note;
        FeasBox.Visibility = string.IsNullOrWhiteSpace(_frame.Note) ? Visibility.Collapsed : Visibility.Visible;
        QuestionsPanel.Children.Clear();
        foreach (var q in _frame.Questions)
        {
            QuestionsPanel.Children.Add(new TextBlock { Text = q, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) });
            var box = new TextBox { Tag = L.T("goal.answer.ph") };
            QuestionsPanel.Children.Add(box);
        }
        Show(StepQuestions);
        QuestionsPanel.Children.OfType<TextBox>().FirstOrDefault()?.Focus();
    }

    private async void MakePlan_Click(object sender, RoutedEventArgs e)
    {
        var boxes = QuestionsPanel.Children.OfType<TextBox>().ToList();
        _goal.Answers = _frame!.Questions.Select((q, i) => new GoalQuestion { Question = q, Answer = i < boxes.Count ? boxes[i].Text.Trim() : "" }).ToList();
        await Run(PlanStep);
    }

    private async void SkipQuestions_Click(object sender, RoutedEventArgs e)
    {
        _goal.Answers = [];
        await Run(PlanStep);
    }

    private async Task PlanStep()
    {
        ShowBusy(L.T("goal.busy.plan"));
        var llm = Llm();
        var plan = await GoalAssistant.PlanAsync(llm, _goal, _cts!.Token);
        _goal.Title = string.IsNullOrWhiteSpace(_frame?.Title) ? plan.Title : _frame!.Title;
        _goal.Summary = plan.Summary;
        _goal.Phases = plan.Phases;
        _sessions = plan.Sessions;
        _waveFrom = _goal.Start;
        ShowPreview(plan.Title, plan.Summary, llm.IsOnline);
    }

    /// <summary>La suite d'un objectif existant : deux nouvelles semaines, adaptées à ce qui a été fait.</summary>
    private async Task NextStep()
    {
        ShowBusy(L.T("goal.busy.next"));
        var llm = Llm();
        var today = DateOnly.FromDateTime(DateTime.Now);
        _waveFrom = _goal.PlannedUntil >= today ? _goal.PlannedUntil.AddDays(1) : today;
        _sessions = await GoalAssistant.NextAsync(llm, _goal, _waveFrom, _cts!.Token);
        ShowPreview(_goal.Title, _goal.Summary, llm.IsOnline);
    }

    private ILlm Llm() => GoalAssistant.Create(_online);

    // ===================== Aperçu =====================

    private void ShowPreview(string title, string summary, bool online)
    {
        PTitle.Text = title;
        PSummary.Text = summary;
        PSummary.Visibility = string.IsNullOrWhiteSpace(summary) ? Visibility.Collapsed : Visibility.Visible;
        POrigin.Text = online ? L.T("goal.origin.online") : L.T("goal.origin.local");

        RoadmapPanel.Children.Clear();
        RoadmapLabel.Visibility = _isNextWave ? Visibility.Collapsed : Visibility.Visible;
        if (!_isNextWave)
        {
            var from = _goal.Start;
            for (int i = 0; i < _goal.Phases.Count; i++)
            {
                var p = _goal.Phases[i];
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var num = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top,
                                       Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                num.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
                DockPanel.SetDock(num, Dock.Left);
                row.Children.Add(num);
                var weeks = new TextBlock { Text = L.P("goal.weeks", (int)Math.Round(Math.Max(1, p.Weeks), MidpointRounding.AwayFromZero)), FontSize = 12, Margin = new Thickness(12, 2, 0, 0) };
                weeks.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");
                DockPanel.SetDock(weeks, Dock.Right);
                row.Children.Add(weeks);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                var sum = new TextBlock { Text = p.Summary, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                sum.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
                text.Children.Add(sum);
                row.Children.Add(text);
                RoadmapPanel.Children.Add(row);
            }
        }

        (_placed, var unplaced) = GoalScheduler.Place(_goal, _sessions, _waveFrom);
        SessionsPanel.Children.Clear();
        _keep = [];
        foreach (var t in _placed)
        {
            var card = new Border { Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "ZoneRowBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            card.SetResourceReference(Border.CornerRadiusProperty, "Radius");
            var keep = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0), ToolTip = L.T("goal.keep.tip") };
            keep.SetResourceReference(StyleProperty, "RoundCheck");
            _keep.Add(keep);
            var grid = new DockPanel();
            DockPanel.SetDock(keep, Dock.Left);
            grid.Children.Add(keep);

            var when = new TextBlock { Width = 150, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                                       Text = t.Date.ToString("ddd d MMM", L.Culture) + "\n" + t.TimeRange };
            when.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            DockPanel.SetDock(when, Dock.Left);
            grid.Children.Add(when);

            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var notes = new TextBlock { Text = t.Notes, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), LineHeight = 18 };
            notes.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
            body.Children.Add(notes);
            if (t.Links.Count > 0)
            {
                var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var link in t.Links)
                {
                    var b = new Button { Content = "↗ " + link.Title, FontSize = 12, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = link.Target };
                    b.SetResourceReference(StyleProperty, "Ghost");
                    b.Click += (_, _) => Launcher.Open(link.Target);
                    links.Children.Add(b);
                }
                body.Children.Add(links);
            }
            grid.Children.Add(body);
            card.Child = grid;
            keep.Checked += (_, _) => { card.Opacity = 1; UpdateAddButton(); };
            keep.Unchecked += (_, _) => { card.Opacity = 0.45; UpdateAddButton(); };
            SessionsPanel.Children.Add(card);
        }
        UnplacedText.Text = unplaced.Count > 0 ? L.P("goal.unplaced", unplaced.Count) : "";
        UnplacedText.Visibility = unplaced.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAddButton();
        Show(StepPreview);
        Scroll.ScrollToTop();
    }

    /// <summary>Captures d'écran (--snapshot) : affiche un aperçu déjà généré.</summary>
    internal void ShowPreviewForSnapshot(Goal g, List<GoalSession> sessions)
    {
        _goal = g;
        _sessions = sessions;
        _waveFrom = g.Start;
        _isNextWave = false;
        ShowPreview(g.Title, g.Summary, online: false);
    }

    private void UpdateAddButton()
    {
        int n = _keep.Count(k => k.IsChecked == true);
        AddBtn.Content = L.P("goal.add", n);
        AddBtn.IsEnabled = n > 0;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var kept = _placed.Where((_, i) => _keep[i].IsChecked == true).ToList();
        GoalScheduler.Commit(_goal, kept, _sessions);
        DoneTitle.Text = L.P("goal.done", kept.Count);
        Show(StepDone);
        BuildGoalList();
    }

    private void SeePlanning_Click(object sender, RoutedEventArgs e)
    {
        var first = Storage.Data.Tasks.Where(t => t.GoalId == _goal.Id && !t.Done).OrderBy(t => t.Date).FirstOrDefault();
        App.Current.MainWin?.Navigate("planning", first?.Date ?? DateOnly.FromDateTime(DateTime.Now));
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        RequestBox.Text = "";
        Show(StepForm);
        UpdateModes();
        RequestBox.Focus();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        Show(StepForm);
        UpdateModes();
    }

    // ===================== Attente et erreurs =====================

    /// <summary>Lance une étape avec annulation, et affiche une erreur lisible si ça coince.</summary>
    private async Task Run(Func<Task> step)
    {
        _retry = step;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        try { await step(); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { Show(_isNextWave ? StepForm : StepForm); }
        catch (Exception ex)
        {
            ErrorText.Text = Explain(ex);
            ErrorDetail.Text = ex.Message;
            Show(StepError);
        }
        finally { _busyTimer.Stop(); }
    }

    private string Explain(Exception ex) => ex switch
    {
        LlmException { Message: "refusal" } => L.T("goal.err.refusal"),
        LlmException l when l.Message.Contains("401") || l.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) => L.T("goal.err.key"),
        System.Net.Http.HttpRequestException when _online => L.T("goal.err.offline"),
        System.Net.Http.HttpRequestException => L.T("goal.err.localServer"),
        TimeoutException => L.T("goal.err.timeout"),
        LlmException => L.T("goal.err.answer"),
        _ => L.T("goal.err.generic"),
    };

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_retry != null) await Run(_retry);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Show(StepForm);
        UpdateModes();
    }

    private void ShowBusy(string title)
    {
        BusyTitle.Text = title;
        BusySub.Text = _online ? L.T("goal.busy.online") : L.T(Storage.Settings.Assistant.LocalSource == "builtin" ? "goal.busy.local" : "goal.busy.localServer");
        _busySince = DateTime.Now;
        BusyTime.Text = "";
        _busyTimer.Start();
        Show(StepBusy);
    }

    private void Show(FrameworkElement step)
    {
        foreach (var s in new FrameworkElement[] { StepForm, StepBusy, StepQuestions, StepPreview, StepDone, StepError })
            s.Visibility = s == step ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== Mes objectifs =====================

    private void BuildGoalList()
    {
        GoalList.Children.Clear();
        var goals = Storage.Data.Goals.OrderByDescending(g => g.Created).ToList();
        MineLabel.Visibility = goals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var g in goals)
        {
            var tasks = Storage.Data.Tasks.Where(t => t.GoalId == g.Id).ToList();
            int done = tasks.Count(t => t.Done), upcoming = tasks.Count(t => !t.Done && !t.Floating && t.Date >= today);

            var card = new Border();
            card.SetResourceReference(StyleProperty, "Card");
            card.Margin = new Thickness(0, 0, 0, 12);
            var stack = new StackPanel();

            var head = new DockPanel();
            var del = new Button { Content = "", ToolTip = L.T("goal.delete.tip"), Width = 30, Height = 30 };
            del.SetResourceReference(StyleProperty, "IconButton");
            del.Click += (_, _) => DeleteGoal(g);
            DockPanel.SetDock(del, Dock.Right);
            head.Children.Add(del);
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = "🎯 " + g.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var meta = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                                       Text = L.F("goal.meta", g.Deadline.ToString("d MMMM", L.Culture), L.P("goal.doneCount", done), L.P("goal.upcoming", upcoming)) };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
            titles.Children.Add(meta);
            head.Children.Add(titles);
            stack.Children.Add(head);

            // Où en est-on dans la feuille de route (sans pourcentage : juste l'étape actuelle).
            if (g.Phases.Count > 0)
            {
                int phase = CurrentPhase(g);
                var p = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
                                        Text = L.F("goal.phaseNow", phase + 1, g.Phases.Count, g.Phases[phase].Title) };
                stack.Children.Add(p);
            }

            // Retour rapide pour ajuster la suite.
            var fb = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var fbLabel = new TextBlock { Text = L.T("goal.feedback"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 8), FontSize = 12.5 };
            fbLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
            fb.Children.Add(fbLabel);
            var last = g.Feedback.LastOrDefault();
            foreach (var key in new[] { "easy", "right", "hard" })
            {
                var rb = new RadioButton { Content = L.T("goal.fb." + key), GroupName = "fb" + g.Id, IsChecked = last == key };
                rb.SetResourceReference(StyleProperty, "Chip");
                rb.Checked += (_, _) => { if (g.Feedback.LastOrDefault() != key) { g.Feedback.Add(key); Storage.Save(); } };
                fb.Children.Add(rb);
            }
            stack.Children.Add(fb);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var next = new Button { Content = L.T("goal.next") };
            next.SetResourceReference(StyleProperty, g.PlannedUntil <= today.AddDays(3) ? "Primary" : "BaseButton");
            next.IsEnabled = g.Deadline > g.PlannedUntil && (GoalAssistant.LocalReady || GoalAssistant.OnlineReady);
            next.ToolTip = g.Deadline <= g.PlannedUntil ? L.T("goal.next.complete") : L.T("goal.next.tip");
            next.Click += async (_, _) =>
            {
                _goal = g;
                _isNextWave = true;
                _online = g.Online && GoalAssistant.OnlineReady;
                Scroll.ScrollToTop();
                await Run(NextStep);
            };
            actions.Children.Add(next);
            var see = new Button { Content = L.T("goal.done.see"), Margin = new Thickness(8, 0, 0, 0) };
            see.SetResourceReference(StyleProperty, "Ghost");
            see.Click += (_, _) => { _goal = g; SeePlanning_Click(see, new RoutedEventArgs()); };
            actions.Children.Add(see);
            stack.Children.Add(actions);

            card.Child = stack;
            GoalList.Children.Add(card);
        }
    }

    private static int CurrentPhase(Goal g)
    {
        double weeks = (DateOnly.FromDateTime(DateTime.Now).DayNumber - g.Start.DayNumber) / 7.0, acc = 0;
        for (int i = 0; i < g.Phases.Count; i++)
        {
            acc += Math.Max(0.5, g.Phases[i].Weeks);
            if (weeks < acc) return i;
        }
        return g.Phases.Count - 1;
    }

    private void DeleteGoal(Goal g)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var removed = Storage.Data.Tasks.Where(t => t.GoalId == g.Id && !t.Done && (t.Date >= today || t.Floating)).ToList();
        int index = Storage.Data.Goals.IndexOf(g);
        GoalScheduler.Remove(g);
        BuildGoalList();
        Undo.Show(L.F("undo.deleted", g.Title), () =>
        {
            Storage.Data.Goals.Insert(Math.Min(index, Storage.Data.Goals.Count), g);
            Storage.Data.Tasks.AddRange(removed);
            Storage.Save();
            BuildGoalList();
        });
    }
}
