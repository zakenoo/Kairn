using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Report des tâches non terminées vers la prochaine session de travail.
/// Rien n'est perdu ni compté comme un échec : la tâche devient « à reprendre »,
/// sans horaire fixe, sur le prochain jour où du travail est prévu.
/// </summary>
public static class CarryOver
{
    /// <summary>
    /// Jour de la prochaine session de travail.
    /// <paramref name="afterNow"/> : ne compte que les blocs qui commencent après maintenant
    /// (report manuel en cours de journée). Sinon, n'importe quel bloc d'aujourd'hui compte (report au changement de jour).
    /// </summary>
    public static DateOnly NextSessionDate(bool afterNow)
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var next = Storage.Data.Tasks
            .Where(t => !t.IsBreak && !t.Floating && !t.IsExternal && (t.Date > today || (t.Date == today && (!afterNow || t.Start > now.TimeOfDay))))
            .OrderBy(t => t.Date).ThenBy(t => t.Start)
            .FirstOrDefault();
        if (next != null) return next.Date;
        // Aucune session prévue : aujourd'hui au changement de jour (pour la voir tout de suite), demain sinon.
        return afterNow ? today.AddDays(1) : today;
    }

    /// <summary>Reporte une tâche à la prochaine session.</summary>
    public static DateOnly Postpone(PlanTask task)
    {
        var target = NextSessionDate(afterNow: true);
        Move(task, target);
        Storage.Save();
        return target;
    }

    /// <summary>Reporte toutes les tâches de travail non terminées d'un jour donné.</summary>
    public static DateOnly PostponeRemaining(DateOnly day)
    {
        var target = NextSessionDate(afterNow: true);
        // Les routines restent où elles sont : demain a déjà la sienne.
        foreach (var t in Storage.Data.Tasks.Where(t => t.Date == day && !t.Done && !t.IsBreak && !t.IsExternal && t.RoutineId is null).ToList())
            Move(t, target);
        Storage.Save();
        return target;
    }

    /// <summary>
    /// Au lancement et à chaque nouveau jour : tout travail non terminé d'un jour passé
    /// glisse vers la prochaine session. Renvoie le nombre de tâches reportées.
    /// </summary>
    public static int RollOver()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        // Une routine manquée ne s'accumule pas : demain revient tout seul, inutile d'y ajouter celle d'hier.
        var pending = Storage.Data.Tasks.Where(t => t.Date < today && !t.Done && !t.IsBreak && !t.IsExternal && t.RoutineId is null).ToList();
        if (pending.Count == 0) return 0;
        var target = NextSessionDate(afterNow: false);
        foreach (var t in pending) Move(t, target);
        Storage.Save();
        return pending.Count;
    }

    /// <summary>
    /// « Ma journée a dérapé » : ce dont l'heure est passée sans être fait retrouve un créneau libre plus tard aujourd'hui,
    /// dans l'ordre prévu, sans toucher à ce qui est déjà calé (rendez-vous compris). Ce qui ne rentre plus attend
    /// dans « À reprendre ». Aucune question : un seul clic, au moment où on a le moins d'énergie pour décider.
    /// </summary>
    public static (int Placed, int Waiting, DateOnly Target) Replan()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var dayEnd = new TimeSpan(22, 30, 0);
        var gap = TimeSpan.FromMinutes(5);
        // Cinq minutes pour souffler avant de reprendre, sur un multiple de cinq.
        var earliest = TimeSpan.FromMinutes(Math.Ceiling((now.TimeOfDay + gap).TotalMinutes / 5) * 5);

        var day = Storage.Data.Tasks.Where(t => t.Date == today && !t.Floating).ToList();
        var missed = day.Where(t => !t.Done && !t.IsBreak && !t.IsExternal && t.End > t.Start && t.End <= now.TimeOfDay)
                        .OrderBy(t => t.Start).ToList();
        if (missed.Count == 0) return (0, 0, today);

        var busy = day.Except(missed)
            .Where(t => t.End <= t.Start || t.End > now.TimeOfDay)
            .Select(t => (From: t.Start, To: t.End > t.Start ? t.End : TimeSpan.FromHours(24)))
            .ToList();

        int placed = 0;
        var waiting = new List<PlanTask>();
        foreach (var t in missed)
        {
            var length = t.Duration;
            TimeSpan? slot = null;
            for (var s = earliest; s + length <= dayEnd; s += gap)
            {
                var e = s + length;
                if (busy.Any(b => s < b.To + gap && e + gap > b.From)) continue;
                slot = s;
                break;
            }
            if (slot is { } start)
            {
                t.Start = start;
                t.End = start + length;
                t.CarriedFrom = null;
                busy.Add((t.Start, t.End));
                placed++;
            }
            else waiting.Add(t);
        }

        var target = today;
        if (waiting.Count > 0)
        {
            target = NextSessionDate(afterNow: true);
            foreach (var t in waiting) Move(t, target);
        }
        Storage.Save();
        return (placed, waiting.Count, target);
    }

    private static void Move(PlanTask t, DateOnly target)
    {
        t.CarriedFrom ??= t.Date;
        t.Date = target;
        t.Floating = true;
        t.IsCurrent = false;
        t.IsPast = false;
    }

    public static string DayName(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (d == today) return L.T("day.laterToday");
        if (d == today.AddDays(1)) return L.T("day.tomorrow");
        return d.ToString("dddd d MMMM", L.Culture);
    }
}
