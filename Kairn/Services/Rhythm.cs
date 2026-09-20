using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Rythme de travail d'une tâche longue : Pomodoro (25 min / 5 min, grande pause toutes les 4 sessions), 50/10…
/// Le découpage est calculé à partir de l'horaire de la tâche, rien n'est stocké à part la formule (« 25/5/15/4 »).
/// </summary>
public static class Rhythm
{
    /// <summary>Formule d'un rythme : minutes de travail, de pause, de grande pause, et toutes les combien de sessions.</summary>
    public sealed record Spec(int Work, int Pause, int LongPause = 0, int LongEvery = 0)
    {
        public override string ToString() =>
            LongPause > 0 && LongEvery > 0 ? $"{Work}/{Pause}/{LongPause}/{LongEvery}" : $"{Work}/{Pause}";
        public string Short => $"{Work}/{Pause}";
    }

    public sealed record Preset(string Key, string Code);

    /// <summary>Rythmes proposés (le nom est une clé de traduction).</summary>
    public static readonly Preset[] Presets =
    [
        new("rhythm.preset.pomodoro", "25/5/15/4"),
        new("rhythm.preset.small", "15/5"),
        new("rhythm.preset.long", "50/10"),
        new("rhythm.preset.deep", "90/20"),
    ];

    /// <summary>Valeur de PlanTask.Rhythm qui refuse le rythme automatique pour cette tâche.</summary>
    public const string Off = "off";

    /// <summary>Une session de travail doit durer au moins ça : sinon on prolonge la précédente plutôt que d'ajouter une miette.</summary>
    private static readonly TimeSpan MinWork = TimeSpan.FromMinutes(10);

    public sealed record Segment(bool IsPause, bool IsLong, TimeSpan From, TimeSpan To, int Session)
    {
        public TimeSpan Length => To - From;
    }

    public static Spec? Parse(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == Off) return null;
        var p = code.Split('/');
        int At(int i) => i < p.Length && int.TryParse(p[i], out var v) ? v : 0;
        int w = At(0), b = At(1);
        if (w < 5 || w > 240 || b < 1 || b > 120) return null;
        int lb = At(2), every = At(3);
        return lb > 0 && every > 1 ? new Spec(w, b, lb, every) : new Spec(w, b);
    }

    /// <summary>Rythme réellement appliqué : celui de la tâche, ou le rythme automatique des tâches longues.</summary>
    public static Spec? For(PlanTask t)
    {
        if (t.IsBreak || t.Floating || t.IsExternal) return null; // un rendez-vous ne se découpe pas en pomodoros
        if (t.Rhythm == Off) return null;
        var spec = t.Rhythm != null ? Parse(t.Rhythm)
            : t.Duration.TotalMinutes >= Storage.Settings.AutoRhythmMinMinutes ? Parse(Storage.Settings.AutoRhythm) : null;
        return spec != null && Segments(t.Duration, spec).Count > 1 ? spec : null;
    }

    /// <summary>Découpage d'un bloc en sessions et pauses, en décalage depuis le début du bloc.</summary>
    public static List<Segment> Segments(TimeSpan total, Spec spec)
    {
        var list = new List<Segment>();
        var work = TimeSpan.FromMinutes(spec.Work);
        var cursor = TimeSpan.Zero;
        int session = 0;
        while (cursor < total)
        {
            session++;
            var workEnd = cursor + work;
            bool isLong = spec.LongEvery > 1 && session % spec.LongEvery == 0;
            var pause = TimeSpan.FromMinutes(isLong ? spec.LongPause : spec.Pause);
            // Plus assez de place pour une pause suivie d'une vraie session : on termine le bloc sur celle-ci.
            if (total - (workEnd + pause) < MinWork)
            {
                list.Add(new Segment(false, false, cursor, total, session));
                break;
            }
            list.Add(new Segment(false, false, cursor, workEnd, session));
            list.Add(new Segment(true, isLong, workEnd, workEnd + pause, session));
            cursor = workEnd + pause;
        }
        return list;
    }

    public static List<Segment> Segments(PlanTask t) => For(t) is { } s ? Segments(t.Duration, s) : [];

    /// <summary>Temps écoulé depuis le début de la tâche (gère les blocs qui passent minuit).</summary>
    public static TimeSpan Elapsed(PlanTask t, DateTime now)
    {
        var e = now.TimeOfDay - t.Start;
        if (e < TimeSpan.Zero) e += TimeSpan.FromDays(1);
        return e;
    }

    /// <summary>Phase en cours (null si la tâche n'a pas de rythme).</summary>
    public static Segment? Current(PlanTask t, DateTime now)
    {
        var segs = Segments(t);
        if (segs.Count == 0) return null;
        var e = Elapsed(t, now);
        return segs.FirstOrDefault(s => e >= s.From && e < s.To) ?? segs[^1];
    }

    public static bool InPause(PlanTask t, DateTime now) => Current(t, now) is { IsPause: true };

    public static int Sessions(List<Segment> segs) => segs.Count(s => !s.IsPause);

    /// <summary>Temps de travail réel (sans les pauses) : c'est lui qui compte dans le cumul.</summary>
    public static TimeSpan WorkTime(PlanTask t)
    {
        var segs = Segments(t);
        return segs.Count == 0 ? t.Duration : segs.Where(s => !s.IsPause).Aggregate(TimeSpan.Zero, (a, s) => a + s.Length);
    }

    /// <summary>« 4 sessions de 25 min, pause de 5 min entre chaque. » pour l'éditeur.</summary>
    public static string Describe(TimeSpan total, Spec spec)
    {
        var segs = Segments(total, spec);
        int n = Sessions(segs);
        if (n <= 1) return L.T("rhythm.preview.tooShort");
        var text = L.P("rhythm.preview", n, PlanTask.FormatDuration(TimeSpan.FromMinutes(spec.Work)),
                       PlanTask.FormatDuration(TimeSpan.FromMinutes(spec.Pause)));
        if (segs.Any(s => s.IsLong))
            text += " " + L.F("rhythm.preview.long", PlanTask.FormatDuration(TimeSpan.FromMinutes(spec.LongPause)), spec.LongEvery);
        return text;
    }

    /// <summary>Petit badge « 25/5 » affiché à côté de la durée dans le planning.</summary>
    public static string Badge(PlanTask t) => For(t) is { } s ? s.Short : "";
}
