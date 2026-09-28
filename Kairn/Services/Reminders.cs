using System.Windows.Threading;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Prévient avant qu'une tâche commence : une heure avant, un quart d'heure avant, à l'heure pile,
/// autant de fois qu'on veut. Quand on ne sent pas le temps passer, c'est ça qui fait la différence
/// entre une tâche planifiée et une tâche faite.
/// Rien n'est stocké : les moments sont recalculés à chaque passage à partir des horaires.
/// </summary>
public static class Reminders
{
    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
    /// <summary>Dernier instant examiné : on ne déclenche que ce qui tombe entre lui et maintenant.</summary>
    private static DateTime _last = DateTime.Now;
    /// <summary>Rappels repoussés à la main (« redis-le-moi dans 10 min »).</summary>
    private static readonly List<(PlanTask Task, DateTime When)> Snoozed = [];

    /// <summary>Choix proposés dans l'éditeur, en minutes avant le début (0 = à l'heure pile).</summary>
    public static readonly int[] Choices = [60, 30, 15, 5, 0];

    /// <summary>Levé quand il est temps de prévenir : la tâche et le nombre de minutes qui restent.</summary>
    public static event Action<PlanTask, int>? Due;

    public static void Start()
    {
        _last = DateTime.Now; // ce qui est déjà passé pendant que l'app était fermée ne se rattrape pas
        Timer.Tick += (_, _) => Tick();
        Timer.Start();
    }

    /// <summary>
    /// Rappels d'une tâche : les siens dès qu'elle en a choisi, sinon ceux des réglages.
    /// Une tâche qui a décoché tous ses rappels garde le droit de n'en avoir aucun.
    /// </summary>
    public static IReadOnlyList<int> For(PlanTask t) => t.Reminders ?? Storage.Settings.DefaultReminders;

    /// <summary>« Pas maintenant » : le même rappel revient dans quelques minutes.</summary>
    public static void Snooze(PlanTask t, int minutes)
    {
        Snoozed.RemoveAll(s => s.Task.Id == t.Id);
        Snoozed.Add((t, DateTime.Now.AddMinutes(minutes)));
    }

    public static void Cancel(PlanTask t) => Snoozed.RemoveAll(s => s.Task.Id == t.Id);

    /// <summary>Rappels à taire, par tâche, jusqu'à cet instant.</summary>
    private static readonly Dictionary<string, DateTime> Muted = [];

    /// <summary>
    /// Tâche lancée à la main à l'instant (« passer à la suivante ») : pas de rappel « c'est l'heure » dans la foulée.
    /// Si elle est redéplacée plus tard dans la journée, ses rappels reviennent normalement.
    /// </summary>
    public static void Mute(PlanTask t, DateTime until)
    {
        Cancel(t);
        Muted[t.Id] = until;
    }

    private static void Tick()
    {
        var now = DateTime.Now;
        var from = _last;
        _last = now;
        // Changement d'heure, veille prolongée, horloge remise : on repart de maintenant sans rien déclencher.
        if (now <= from || now - from > TimeSpan.FromMinutes(10)) return;

        foreach (var (task, when) in Snoozed.Where(s => s.When > from && s.When <= now).ToList())
        {
            Snoozed.RemoveAll(s => s.Task.Id == task.Id);
            if (!task.Done) Raise(task, (int)Math.Round((task.Date.ToDateTime(TimeOnly.FromTimeSpan(task.Start)) - now).TotalMinutes));
        }

        var today = DateOnly.FromDateTime(now);
        foreach (var id in Muted.Where(m => now - m.Value > TimeSpan.FromHours(1)).Select(m => m.Key).ToList()) Muted.Remove(id);
        foreach (var task in Storage.Data.Tasks)
        {
            if (task.Done || task.IsBreak || task.Floating) continue;
            if (task.Date < today || task.Date > today.AddDays(1)) continue;
            var start = task.Date.ToDateTime(TimeOnly.FromTimeSpan(task.Start));
            var muted = Muted.GetValueOrDefault(task.Id, DateTime.MinValue);
            foreach (var minutes in For(task))
            {
                var moment = start.AddMinutes(-minutes);
                if (moment > from && moment <= now && moment > muted) Raise(task, minutes);
            }
        }
    }

    private static void Raise(PlanTask task, int minutes) => Due?.Invoke(task, Math.Max(0, minutes));
}
