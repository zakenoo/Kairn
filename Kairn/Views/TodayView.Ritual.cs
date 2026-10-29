using System.Windows;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// Deux rituels de trente secondes. Le matin : la seule chose par laquelle commencer.
/// Le soir : ce qui reste se pose pour la suite, et la tête peut s'arrêter.
/// Une carte, un bouton, et elle ne revient pas avant le lendemain. Rien n'est compté contre personne.
/// </summary>
public partial class TodayView
{
    private enum RitualKind { None, Morning, Evening }

    private RitualKind _ritual;
    private int _ritualHour = -1;
    private PlanTask? _ritualFirst;

    private void UpdateRitual()
    {
        var now = DateTime.Now;
        _ritualHour = now.Hour;
        var s = Storage.Settings;
        var kind = RitualKind.None;
        if (s.Rituals && !s.FocusOnly)
        {
            if (now.Hour >= 20 && s.EveningDone != _day) kind = RitualKind.Evening;
            else if (now.Hour is >= 4 and < 12 && s.MorningDone != _day) kind = RitualKind.Morning;
        }
        _ritual = kind;
        RitualCard.Visibility = kind == RitualKind.None ? Visibility.Collapsed : Visibility.Visible;
        if (kind == RitualKind.Morning) ShowMorning(now.TimeOfDay);
        else if (kind == RitualKind.Evening) ShowEvening();
    }

    private void ShowMorning(TimeSpan now)
    {
        RitualIcon.Text = "☀️";
        RitualTitle.Text = L.T("ritual.morning.title");
        RitualClose.Visibility = Visibility.Visible;

        var open = _tasks.Where(x => !x.Done && !x.IsBreak && !x.IsExternal && (x.End > now || x.End <= x.Start)).ToList();
        var carried = _carried.Where(x => !x.Done && !x.IsBreak).ToList();
        // Commencer par ce qui a été étoilé, sinon par ce qui vient en premier, sinon par ce qui attend.
        _ritualFirst = open.FirstOrDefault(x => x.Pinned) ?? open.FirstOrDefault() ?? carried.FirstOrDefault();

        if (_ritualFirst is null)
        {
            RitualText.Text = L.T("ritual.morning.empty");
            RitualSub.Text = L.T("ritual.morning.empty.sub");
            RitualMain.Content = L.T("today.empty.plan");
            RitualClose.Content = L.T("ritual.ok");
            return;
        }

        RitualText.Text = L.F("ritual.morning.start", (_ritualFirst.HasEmoji ? _ritualFirst.Emoji + " " : "") + _ritualFirst.Title);
        RitualSub.Text = L.P("ritual.morning.count", open.Count + carried.Count);
        bool running = !_ritualFirst.Floating && _ritualFirst.Start <= now;
        RitualMain.Content = L.T(running ? "ritual.ok" : "ritual.morning.now");
        RitualClose.Content = L.T("ritual.later");
        RitualClose.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowEvening()
    {
        RitualIcon.Text = "🌙";
        RitualTitle.Text = L.T("ritual.evening.title");
        int left = RitualLeft().Count;
        int done = _tasks.Concat(_carried).Count(x => x.Done && !x.IsBreak && !x.IsExternal);
        RitualSub.Text = done > 0 ? L.P("ritual.evening.done", done) : L.T("ritual.evening.rest");
        if (left > 0)
        {
            RitualText.Text = L.P("ritual.evening.left", left);
            RitualMain.Content = L.T("ritual.evening.put");
            RitualClose.Content = L.T("ritual.evening.keep");
            RitualClose.Visibility = Visibility.Visible;
        }
        else
        {
            RitualText.Text = L.T("ritual.evening.clear");
            RitualMain.Content = L.T("ritual.evening.night");
            RitualClose.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Ce qui reste à faire aujourd'hui. Les routines n'en font pas partie : elles reviennent d'elles-mêmes.</summary>
    private List<PlanTask> RitualLeft() =>
        _tasks.Concat(_carried).Where(x => !x.Done && !x.IsBreak && !x.IsExternal && x.RoutineId is null).ToList();

    private void RitualMain_Click(object sender, RoutedEventArgs e)
    {
        var kind = _ritual;
        var first = _ritualFirst;
        DismissRitual();
        if (kind == RitualKind.Morning)
        {
            if (first is null) { Plan_Click(sender, e); return; }
            if (first.Floating || first.Start > DateTime.Now.TimeOfDay)
            {
                first.Floating = false;
                StartNow(first);
            }
        }
        else if (kind == RitualKind.Evening)
        {
            int n = RitualLeft().Count;
            if (n > 0) Postponed(CarryOver.PostponeRemaining(_day), n);
        }
    }

    private void RitualClose_Click(object sender, RoutedEventArgs e) => DismissRitual();

    private void DismissRitual()
    {
        if (_ritual == RitualKind.Morning) Storage.Settings.MorningDone = _day;
        else if (_ritual == RitualKind.Evening) Storage.Settings.EveningDone = _day;
        Storage.SaveSettings();
        UpdateRitual();
    }
}
