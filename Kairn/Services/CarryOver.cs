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
        foreach (var t in Storage.Data.Tasks.Where(t => t.Date == day && !t.Done && !t.IsBreak && !t.IsExternal).ToList())
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
        var pending = Storage.Data.Tasks.Where(t => t.Date < today && !t.Done && !t.IsBreak && !t.IsExternal).ToList();
        if (pending.Count == 0) return 0;
        var target = NextSessionDate(afterNow: false);
        foreach (var t in pending) Move(t, target);
        Storage.Save();
        return pending.Count;
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
