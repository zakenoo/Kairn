using System.Windows;
using System.Windows.Controls;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>Choix du rythme d'une tâche dans l'éditeur : aucun, automatique, Pomodoro & co, ou perso.</summary>
public partial class PlanningView
{
    private const string RhythmNone = "none", RhythmAuto = "auto", RhythmCustom = "custom";
    private bool _rhythmReady;

    private void BuildRhythmChips(string? current)
    {
        _rhythmReady = false;
        var auto = Rhythm.Parse(Storage.Settings.AutoRhythm);
        var spec = Rhythm.Parse(current);
        string selected = current switch
        {
            null => auto != null ? RhythmAuto : RhythmNone,
            Rhythm.Off => RhythmNone,
            _ when spec is null => RhythmNone,
            _ => Rhythm.Presets.FirstOrDefault(p => p.Code == spec.ToString())?.Code ?? RhythmCustom
        };

        EdRhythm.Children.Clear();
        EdRhythm.Children.Add(Chip(L.T("rhythm.none"), RhythmNone));
        if (auto != null) EdRhythm.Children.Add(Chip(L.F("rhythm.auto", auto.Short), RhythmAuto));
        foreach (var p in Rhythm.Presets) EdRhythm.Children.Add(Chip(L.T(p.Key), p.Code));
        EdRhythm.Children.Add(Chip(L.T("rhythm.custom"), RhythmCustom));

        EdRhythmWork.Text = selected == RhythmCustom ? spec!.Work.ToString() : "";
        EdRhythmPause.Text = selected == RhythmCustom ? spec!.Pause.ToString() : "";
        _rhythmReady = true;
        UpdateRhythmInfo();

        RadioButton Chip(string text, string tag)
        {
            var rb = new RadioButton { Content = text, Tag = tag, GroupName = "edRhythm", IsChecked = tag == selected, Padding = new Thickness(0) };
            rb.SetResourceReference(StyleProperty, "Chip");
            rb.Checked += (_, _) => UpdateRhythmInfo();
            return rb;
        }
    }

    private string SelectedRhythmTag =>
        EdRhythm.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? RhythmNone;

    private Rhythm.Spec? CustomSpec() =>
        int.TryParse(EdRhythmWork.Text.Trim(), out var w) && int.TryParse(EdRhythmPause.Text.Trim(), out var p)
            ? Rhythm.Parse($"{w}/{p}") : null;

    private void Rhythm_Changed(object sender, RoutedEventArgs e) => UpdateRhythmInfo();

    /// <summary>Aperçu en clair : « 4 sessions de 25 min, pause de 5 min entre chaque. »</summary>
    private void UpdateRhythmInfo()
    {
        if (!_rhythmReady) return;
        var tag = SelectedRhythmTag;
        EdRhythmCustom.Visibility = tag == RhythmCustom ? Visibility.Visible : Visibility.Collapsed;

        string info = "";
        if (EdBreak.IsChecked == true) info = tag == RhythmNone ? "" : L.T("rhythm.preview.isBreak");
        else if (tag != RhythmNone && PlanParser.TryParseTime(EdStart.Text, out var start) && PlanParser.TryParseTime(EdEnd.Text, out var end))
        {
            var total = end > start ? end - start : end + TimeSpan.FromDays(1) - start;
            var min = TimeSpan.FromMinutes(Storage.Settings.AutoRhythmMinMinutes);
            var spec = tag switch
            {
                RhythmAuto => Rhythm.Parse(Storage.Settings.AutoRhythm),
                RhythmCustom => CustomSpec(),
                _ => Rhythm.Parse(tag)
            };
            if (tag == RhythmAuto && total < min) info = L.F("rhythm.preview.autoShort", Models.PlanTask.FormatDuration(min));
            else if (spec != null) info = Rhythm.Describe(total, spec);
            else if (tag == RhythmCustom) info = L.T("rhythm.custom.invalid");
        }
        EdRhythmInfo.Text = info;
        EdRhythmInfo.Visibility = info.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Valeur à enregistrer dans PlanTask.Rhythm. Faux si le rythme perso est incomplet.</summary>
    private bool TryReadRhythm(out string? value)
    {
        value = null;
        switch (SelectedRhythmTag)
        {
            case RhythmAuto: return true;
            // « Aucun » alors qu'un rythme automatique existe : on le refuse explicitement pour cette tâche.
            case RhythmNone: value = Rhythm.Parse(Storage.Settings.AutoRhythm) != null ? Rhythm.Off : null; return true;
            case RhythmCustom:
                var spec = CustomSpec();
                value = spec?.ToString();
                return spec != null;
            default: value = SelectedRhythmTag; return true;
        }
    }
}
