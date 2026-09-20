using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>Petit bandeau en bas à droite de l'écran, qui ne vole pas le focus et disparaît tout seul.</summary>
public partial class NudgeWindow : Window
{
    private static NudgeWindow? _open;
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(10) };

    private NudgeWindow()
    {
        InitializeComponent();
        _autoClose.Tick += (_, _) => FadeOut();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth;
            Top = area.Bottom - ActualHeight;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            _autoClose.Start();
        };
        MouseEnter += (_, _) => _autoClose.Stop();
        MouseLeave += (_, _) => _autoClose.Start();
    }

    /// <summary>Rappel du garde : une app de distraction est au premier plan (ou vient d'être fermée).</summary>
    public static void Show(PlanTask task, string app, bool closed)
    {
        var now = DateTime.Now.TimeOfDay;
        var end = task.End > task.Start ? task.End : task.End + TimeSpan.FromDays(1);
        var left = end - now;
        var leftText = left.TotalMinutes < 1 ? L.T("nudge.lessThanMinute") : PlanTask.FormatDuration(TimeSpan.FromMinutes(Math.Ceiling(left.TotalMinutes)));

        var w = new NudgeWindow();
        w.Header.Text = closed ? L.F("nudge.closed", app.ToUpper(L.Culture)) : L.T("nudge.header");
        w.TaskTitle.Text = task.Title;
        w.Detail.Text = closed ? L.F("nudge.closed.detail", leftText) : L.F("nudge.detail", leftText, app);
        Open(w);
    }

    /// <summary>Changement de phase d'une tâche rythmée : l'heure de la pause, ou de reprendre.</summary>
    public static void ShowRhythm(PlanTask task, Rhythm.Segment seg, int sessions)
    {
        var w = new NudgeWindow();
        var length = PlanTask.FormatDuration(seg.Length);
        if (seg.IsPause)
        {
            w.Header.Text = L.T(seg.IsLong ? "rhythm.toast.longPause.header" : "rhythm.toast.pause.header");
            w.TaskTitle.Text = L.F("rhythm.toast.pause.title", length);
            w.Detail.Text = L.T($"rhythm.tip.{seg.Session % TipCount}");
            w._autoClose.Interval = TimeSpan.FromSeconds(20);
        }
        else
        {
            w.Header.Text = L.T("rhythm.toast.work.header");
            w.TaskTitle.Text = task.Title;
            w.Detail.Text = L.F("rhythm.toast.work.detail", seg.Session, sessions, length);
        }
        w.Actions.Visibility = Visibility.Collapsed;
        w.RhythmActions.Visibility = Visibility.Visible;
        if (Storage.Settings.RhythmSound) System.Media.SystemSounds.Asterisk.Play();
        Open(w);
    }

    /// <summary>Nombre de conseils de pause (clés rhythm.tip.0 à rhythm.tip.4).</summary>
    public const int TipCount = 5;

    private PlanTask? _reminderTask;

    /// <summary>Une tâche va commencer : le rappel dit quoi, dans combien de temps, et rien d'autre.</summary>
    public static void ShowReminder(PlanTask task, int minutes)
    {
        var w = new NudgeWindow { _reminderTask = task };
        w.Header.Text = L.T("remind.header");
        w.TaskTitle.Text = (task.HasEmoji ? task.Emoji + "  " : "") + task.Title;
        w.Detail.Text = minutes <= 0
            ? L.F("remind.now", PlanTask.Fmt(task.Start), task.DurationText)
            : L.F("remind.in", PlanTask.FormatDuration(TimeSpan.FromMinutes(minutes)), PlanTask.Fmt(task.Start));
        // La première étape, s'il y en a une : c'est elle qui fait démarrer, pas le titre.
        if (task.NextStep is { } step) w.Detail.Text += "\n" + L.F("remind.firstStep", step.Title);
        w.Actions.Visibility = Visibility.Collapsed;
        w.ReminderActions.Visibility = Visibility.Visible;
        w._autoClose.Interval = TimeSpan.FromSeconds(25);
        if (Storage.Settings.RhythmSound) System.Media.SystemSounds.Asterisk.Play();
        Open(w);
    }

    private void RemindLater_Click(object sender, RoutedEventArgs e)
    {
        if (_reminderTask is { } t) Reminders.Snooze(t, 10);
        FadeOut();
    }

    private static void Open(NudgeWindow w)
    {
        _open?.Close();
        _open = w;
        w.Closed += (s, _) => { if (ReferenceEquals(_open, s)) _open = null; };
        w.Show();
    }

    private void FadeOut()
    {
        _autoClose.Stop();
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowMain();
        FadeOut();
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        App.Current.Guard.SnoozedUntil = DateTime.Now.AddMinutes(15);
        FadeOut();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => FadeOut();
}
