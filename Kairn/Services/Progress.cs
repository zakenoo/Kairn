using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Le sentier : tout ce qui a été fait depuis le début, cumulé.
/// Volontairement sans série à tenir ni compteur qui se casse — un jour sauté ne retire jamais une pierre.
/// Ça ne fait que monter, et ça change lentement l'allure du cairn.
/// </summary>
public static class Progress
{
    /// <summary>Un palier du sentier : à partir de combien de pierres, et jusqu'où le cairn peut monter.</summary>
    public sealed record Tier(int From, string Key, int Cairn);

    public static readonly Tier[] Tiers =
    [
        new(0,    "path.tier.0", 7),
        new(30,   "path.tier.1", 9),
        new(100,  "path.tier.2", 11),
        new(250,  "path.tier.3", 13),
        new(600,  "path.tier.4", 15),
        new(1200, "path.tier.5", 17),
    ];

    /// <summary>Toutes les pierres posées depuis l'installation : une par tâche finie, une par étape finie.</summary>
    public static int Stones()
    {
        int total = 0;
        foreach (var t in Storage.Data.Tasks)
        {
            if (t.IsBreak) continue;
            if (t.Done) total++;
            total += t.Steps.Count(s => s.Done);
        }
        return total;
    }

    public static Tier Current(int stones) => Tiers.Last(t => stones >= t.From);

    public static Tier? Next(int stones) => Tiers.FirstOrDefault(t => t.From > stones);

    /// <summary>Temps réellement travaillé sur une période (les tâches finies seulement).</summary>
    public static TimeSpan WorkedSince(DateOnly from) =>
        Storage.Data.Tasks.Where(t => t.Done && !t.IsBreak && t.Date >= from)
                          .Aggregate(TimeSpan.Zero, (a, t) => a + Rhythm.WorkTime(t));

    /// <summary>Texte du sentier affiché sous le cairn, et son détail au survol.</summary>
    public static (string Label, string Tip) Describe()
    {
        int stones = Stones();
        var tier = Current(stones);
        var next = Next(stones);
        var label = L.T(tier.Key);
        var tip = L.P("path.stones", stones) + (next is null ? "" : "\n" + L.F("path.next", next.From - stones, L.T(next.Key)));
        return (label, tip);
    }
}
