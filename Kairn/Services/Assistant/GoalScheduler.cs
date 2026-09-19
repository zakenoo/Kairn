using Kairn.Models;

namespace Kairn.Services.Assistant;

/// <summary>
/// Place les séances dans les créneaux vraiment libres du calendrier, sans IA : une séance par jour choisi,
/// dans le moment de la journée demandé, sans chevaucher ce qui existe déjà (avec une petite marge).
/// </summary>
public static class GoalScheduler
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    public static (TimeSpan From, TimeSpan To) WindowRange(GoalWindow w) => w switch
    {
        GoalWindow.Morning => (new(8, 0, 0), new(12, 0, 0)),
        GoalWindow.Afternoon => (new(13, 30, 0), new(18, 0, 0)),
        GoalWindow.Evening => (new(18, 30, 0), new(22, 30, 0)),
        _ => (new(8, 0, 0), new(22, 30, 0)),
    };

    /// <summary>Séances placées (tâches pas encore ajoutées) et celles qui n'ont pas trouvé de place avant l'échéance.</summary>
    public static (List<PlanTask> Placed, List<GoalSession> Unplaced) Place(Goal g, IReadOnlyList<GoalSession> sessions, DateOnly from)
    {
        var placed = new List<PlanTask>();
        var (winFrom, winTo) = WindowRange(g.Window);
        var now = DateTime.Now;
        int i = 0;
        for (var day = from; day <= g.Deadline && i < sessions.Count; day = day.AddDays(1))
        {
            if (!g.Days.Contains(day.DayOfWeek)) continue;
            var s = sessions[i];
            var length = TimeSpan.FromMinutes(s.Minutes);
            var busy = Storage.Data.Tasks.Where(t => t.Date == day && !t.Floating)
                .Select(t => (t.Start - Margin, (t.End > t.Start ? t.End : TimeSpan.FromHours(24)) + Margin)).ToList();
            // Aujourd'hui : pas avant une demi-heure, pour avoir le temps de s'y mettre.
            var earliest = winFrom;
            if (day == DateOnly.FromDateTime(now))
            {
                var soon = now.TimeOfDay + TimeSpan.FromMinutes(30);
                soon = TimeSpan.FromMinutes(Math.Ceiling(soon.TotalMinutes / 15) * 15);
                if (soon > earliest) earliest = soon;
            }
            for (var start = earliest; start + length <= winTo; start += Step)
            {
                var end = start + length;
                if (busy.Any(b => start < b.Item2 && end > b.Item1)) continue;
                placed.Add(new PlanTask
                {
                    Date = day, Start = start, End = end, Title = s.Title, GoalId = g.Id, CategoryId = g.CategoryId,
                    Notes = s.Instructions, Section = g.Title,
                    Links = s.Resources.Select(r => new TaskLink { Title = r.Title, Target = r.Target }).ToList(),
                });
                i++;
                break;
            }
        }
        return (placed, sessions.Skip(i).ToList());
    }

    /// <summary>
    /// Ajoute les séances au calendrier et range les ressources dans la bibliothèque :
    /// une catégorie par objectif, une sous-catégorie par phase.
    /// </summary>
    public static void Commit(Goal g, IReadOnlyList<PlanTask> tasks, IReadOnlyList<GoalSession> sessions)
    {
        var cat = Storage.CategoryById(g.CategoryId);
        if (cat is null)
        {
            cat = new Category { Name = g.Title, Emoji = "🎯", Color = Storage.Settings.Theme.Color("Accent") };
            Storage.Data.Categories.Add(cat);
            g.CategoryId = cat.Id;
        }
        foreach (var t in tasks) t.CategoryId = cat.Id;

        foreach (var s in sessions.Where(s => s.Resources.Count > 0))
        {
            var target = cat;
            if (s.Phase < g.Phases.Count)
            {
                var name = g.Phases[s.Phase].Title;
                target = Storage.ChildrenOf(cat).FirstOrDefault(c => c.Name == name)
                         ?? AddSub(cat, name);
            }
            foreach (var r in s.Resources.Where(r => target.Items.All(i => i.Content != r.Target)))
                target.Items.Add(new ResourceItem { Kind = ResourceKind.Link, Title = r.Title, Content = r.Target });
        }

        Storage.Data.Tasks.AddRange(tasks);
        if (tasks.Count > 0) g.PlannedUntil = tasks.Max(t => t.Date);
        if (!Storage.Data.Goals.Contains(g)) Storage.Data.Goals.Add(g);
        Storage.Save();
    }

    private static Category AddSub(Category parent, string name)
    {
        var sub = new Category { Name = name, Emoji = "📂", Color = parent.Color, ParentId = parent.Id };
        Storage.Data.Categories.Add(sub);
        return sub;
    }

    /// <summary>Supprime un objectif et ses séances à venir non faites (ce qui est fait reste dans l'historique).</summary>
    public static int Remove(Goal g)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var future = Storage.Data.Tasks.Where(t => t.GoalId == g.Id && !t.Done && (t.Date >= today || t.Floating)).ToList();
        foreach (var t in future) Storage.Data.Tasks.Remove(t);
        Storage.Data.Goals.Remove(g);
        Storage.Save();
        return future.Count;
    }
}
