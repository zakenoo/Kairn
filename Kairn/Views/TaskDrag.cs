using System.Windows;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// Déplacer une tâche en la faisant glisser : un autre jour, un autre créneau, une autre colonne.
/// Aucun message, aucune confirmation, aucun reproche — c'est exactement ce qu'il faut pour
/// repousser quelque chose à demain sans que ça coûte quoi que ce soit.
/// </summary>
public static class TaskDrag
{
    public const string Format = "Kairn.PlanTask";

    /// <summary>Distance minimale avant de considérer qu'on fait glisser et pas qu'on clique.</summary>
    public static bool Far(Point from, Point to) =>
        Math.Abs(to.X - from.X) > SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(to.Y - from.Y) > SystemParameters.MinimumVerticalDragDistance;

    public static PlanTask? From(IDataObject data) =>
        data.GetDataPresent(Format) ? data.GetData(Format) as PlanTask : null;

    /// <summary>Change une tâche de jour en gardant son horaire.</summary>
    public static void MoveToDay(PlanTask task, DateOnly day)
    {
        if (task.Date == day) return;
        task.Date = day;
        // Un report volontaire n'est pas un échec : la tâche redevient une tâche normale.
        task.CarriedFrom = null;
        Storage.Save();
    }

    /// <summary>Pose une tâche sur un créneau précis, en gardant sa durée.</summary>
    public static void MoveToSlot(PlanTask task, DateOnly day, TimeSpan start)
    {
        var length = task.Duration;
        task.Date = day;
        task.Start = Round(start);
        task.End = task.Start + length;
        task.Floating = false;
        task.CarriedFrom = null;
        Storage.Save();
    }

    /// <summary>Par quart d'heure : viser à la minute près à la souris serait un supplice.</summary>
    public static TimeSpan Round(TimeSpan t)
    {
        var minutes = Math.Clamp(Math.Round(t.TotalMinutes / 15) * 15, 0, 23 * 60 + 45);
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>
    /// Réordonne les tâches d'un jour : les créneaux de la journée ne bougent pas, c'est leur contenu qui change de place.
    /// La forme de la journée est conservée telle qu'elle a été pensée, seul l'ordre change.
    /// </summary>
    public static void Reorder(DateOnly day, PlanTask moved, PlanTask target, bool after)
    {
        var ordered = Storage.Data.Tasks.Where(t => t.Date == day && !t.Floating).OrderBy(t => t.Start).ToList();
        if (!ordered.Contains(moved) || !ordered.Contains(target) || ReferenceEquals(moved, target)) return;

        var slots = ordered.Select(t => (t.Start, t.End)).ToList();
        ordered.Remove(moved);
        int index = ordered.IndexOf(target) + (after ? 1 : 0);
        ordered.Insert(Math.Clamp(index, 0, ordered.Count), moved);

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Start = slots[i].Start;
            ordered[i].End = slots[i].End;
        }
        Storage.Save();
    }
}
