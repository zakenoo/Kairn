using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>Carte « En cours » d'une tâche rythmée (Pomodoro…) : phase, compte à rebours et frise des sessions.</summary>
public partial class TodayView
{
    private string? _rhythmBarKey;
    private readonly List<(Rhythm.Segment Seg, FrameworkElement Track, ScaleTransform? Fill)> _rhythmParts = [];

    /// <summary>Affiche la tâche en cours avec son rythme. Faux si elle n'en a pas.</summary>
    private bool ShowRhythm(PlanTask task, DateTime now, string nextText)
    {
        var segs = Rhythm.Segments(task);
        if (segs.Count == 0) return false;
        var e = Rhythm.Elapsed(task, now);
        var seg = segs.FirstOrDefault(s => e >= s.From && e < s.To) ?? segs[^1];
        int sessions = Rhythm.Sessions(segs);
        bool last = ReferenceEquals(seg, segs[^1]);

        var label = seg.IsPause
            ? L.T(seg.IsLong ? "rhythm.label.longPause" : "rhythm.label.pause")
            : L.F("rhythm.label.work", seg.Session, sessions);
        var remainingLabel = seg.IsPause ? L.T("rhythm.beforeResume") : last ? L.T("today.remaining") : L.T("rhythm.beforePause");
        ShowCard(task, label, Clock(seg.To - e), remainingLabel, 0, isNow: true, rhythm: true);

        NextText.Text = seg.IsPause ? L.T($"rhythm.tip.{seg.Session % NudgeWindow.TipCount}") : nextText;
        UpdateRhythmBar(task, segs, e);
        return true;
    }

    private void UpdateRhythmBar(PlanTask task, List<Rhythm.Segment> segs, TimeSpan elapsed)
    {
        var key = $"{task.Id}|{task.Start}|{task.End}|{task.Rhythm}|{segs.Count}";
        if (key != _rhythmBarKey) BuildRhythmBar(key, segs);
        RhythmBar.Visibility = Visibility.Visible;

        foreach (var (seg, track, fill) in _rhythmParts)
        {
            double f = Math.Clamp((elapsed - seg.From).TotalSeconds / Math.Max(1, seg.Length.TotalSeconds), 0, 1);
            if (fill != null) fill.ScaleX = f;
            else track.Opacity = elapsed >= seg.From && elapsed < seg.To ? 1 : 0.45; // pause en cours : elle s'allume
        }
    }

    private void BuildRhythmBar(string key, List<Rhythm.Segment> segs)
    {
        _rhythmBarKey = key;
        _rhythmParts.Clear();
        RhythmBar.Children.Clear();
        RhythmBar.ColumnDefinitions.Clear();
        for (int i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            RhythmBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(seg.Length.TotalMinutes, GridUnitType.Star) });
            var cell = new Grid { Margin = new Thickness(i == 0 ? 0 : 2, 0, i == segs.Count - 1 ? 0 : 2, 0) };
            Grid.SetColumn(cell, i);

            if (seg.IsPause)
            {
                // Pause : un trait fin, discret, qui s'illumine quand c'est le moment.
                var line = new Border { Height = 2, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Center };
                line.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
                cell.Children.Add(line);
                _rhythmParts.Add((seg, line, null));
            }
            else
            {
                var track = new Border { CornerRadius = new CornerRadius(3) };
                track.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
                var scale = new ScaleTransform(0, 1);
                var fill = new Border { CornerRadius = new CornerRadius(3), RenderTransform = scale, RenderTransformOrigin = new Point(0, 0.5) };
                fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                cell.Children.Add(track);
                cell.Children.Add(fill);
                _rhythmParts.Add((seg, track, scale));
            }
            RhythmBar.Children.Add(cell);
        }
    }
}
