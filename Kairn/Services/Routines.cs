using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Tâches qui reviennent. Kairn pose les occurrences quatre semaines à l'avance et complète à chaque lancement :
/// ce sont de vraies tâches, qui se déplacent, se cochent et se suppriment une par une.
/// Un jour sauté ne laisse aucune trace : rien ne se reporte, rien ne se compte.
/// </summary>
public static class Routines
{
    private const int HorizonDays = 28;

    public static readonly DayOfWeek[] EveryDay =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static readonly DayOfWeek[] Weekdays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public static Routine? Of(PlanTask t) =>
        t.RoutineId is null ? null : Storage.Data.Routines.FirstOrDefault(r => r.Id == t.RoutineId);

    public static bool Same(IEnumerable<DayOfWeek> a, IEnumerable<DayOfWeek> b) => a.ToHashSet().SetEquals(b);

    /// <summary>« Tous les jours », « En semaine », « Chaque lundi », ou la liste des jours.</summary>
    public static string Label(IReadOnlyCollection<DayOfWeek> days)
    {
        if (Same(days, EveryDay)) return L.T("routine.everyDay");
        if (Same(days, Weekdays)) return L.T("routine.weekdays");
        if (days.Count == 1) return L.F("routine.every", L.Culture.DateTimeFormat.GetDayName(days.First()));
        return string.Join(", ", EveryDay.Where(days.Contains).Select(d => L.Culture.DateTimeFormat.GetAbbreviatedDayName(d)));
    }

    /// <summary>Prochain jour (aujourd'hui compris) qui fait partie de la routine.</summary>
    public static DateOnly FirstDay(IReadOnlyCollection<DayOfWeek> days, DateOnly from)
    {
        for (int i = 0; i < 7; i++)
            if (days.Contains(from.AddDays(i).DayOfWeek)) return from.AddDays(i);
        return from;
    }

    /// <summary>Fait de la tâche la première occurrence d'une routine et pose les suivantes.</summary>
    public static Routine Create(PlanTask t, IEnumerable<DayOfWeek> days)
    {
        var r = new Routine
        {
            Title = t.Title, Notes = t.Notes, Emoji = t.Emoji, CategoryId = t.CategoryId, IsBreak = t.IsBreak,
            Start = t.Start, End = t.End, Days = [.. days],
            Reminders = t.Reminders is null ? null : [.. t.Reminders],
            Steps = [.. t.Steps.Select(s => s.Title)],
            Through = t.Date
        };
        t.RoutineId = r.Id;
        Storage.Data.Routines.Add(r);
        Materialize();
        return r;
    }

    /// <summary>
    /// La tâche modifiée devient le modèle : les occurrences à venir qui n'ont pas encore été faites suivent,
    /// avec les jours choisis. Ce qui est passé ou déjà coché ne bouge pas.
    /// </summary>
    public static void Apply(PlanTask t, IEnumerable<DayOfWeek> days)
    {
        if (Of(t) is not { } r) return;
        var list = days.ToList();
        r.Title = t.Title; r.Notes = t.Notes; r.Emoji = t.Emoji; r.CategoryId = t.CategoryId; r.IsBreak = t.IsBreak;
        r.Start = t.Start; r.End = t.End;
        r.Reminders = t.Reminders is null ? null : [.. t.Reminders];
        r.Steps = [.. t.Steps.Select(s => s.Title)];
        r.Days = list;

        Storage.Data.Tasks.RemoveAll(x => x.RoutineId == r.Id && x.Date > t.Date && !x.Done);
        r.Through = t.Date;
        Materialize();
    }

    /// <summary>La routine s'arrête : les occurrences à venir disparaissent, l'historique reste.</summary>
    public static void Stop(PlanTask t)
    {
        if (Of(t) is not { } r) return;
        Storage.Data.Tasks.RemoveAll(x => x.RoutineId == r.Id && x.Date > t.Date && !x.Done);
        foreach (var x in Storage.Data.Tasks.Where(x => x.RoutineId == r.Id)) x.RoutineId = null;
        Storage.Data.Routines.Remove(r);
    }

    /// <summary>
    /// Pose les occurrences manquantes jusqu'à quatre semaines d'avance. Appelée au lancement et à chaque nouveau jour.
    /// Renvoie vrai si quelque chose a été ajouté.
    /// </summary>
    public static bool Materialize()
    {
        if (Storage.Data.Routines.Count == 0) return false;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var horizon = today.AddDays(HorizonDays);
        bool changed = false;
        foreach (var r in Storage.Data.Routines)
        {
            var from = r.Through < today ? today : r.Through.AddDays(1);
            for (var day = from; day <= horizon; day = day.AddDays(1))
            {
                if (!r.Days.Contains(day.DayOfWeek)) continue;
                if (Storage.Data.Tasks.Any(x => x.RoutineId == r.Id && x.Date == day)) continue;
                Storage.Data.Tasks.Add(new PlanTask
                {
                    Date = day, Start = r.Start, End = r.End, Title = r.Title, Notes = r.Notes, Emoji = r.Emoji,
                    CategoryId = r.CategoryId, IsBreak = r.IsBreak, RoutineId = r.Id,
                    Reminders = r.Reminders is null ? null : [.. r.Reminders],
                    Steps = [.. r.Steps.Select(s => new SubTask { Title = s })]
                });
                changed = true;
            }
            if (r.Through < horizon) { r.Through = horizon; changed = true; }
        }
        if (changed) Storage.Save();
        return changed;
    }
}
