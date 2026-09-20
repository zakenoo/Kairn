using System.Text.RegularExpressions;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Transforme un programme écrit en texte libre en tâches.
/// Comprend par exemple :
///   « 9h30 Réviser »,  « 12h00 - 13h00 : Repas »,  « * 14:30–15h Pause »,  « de 9h à 10h30 : Sport ».
/// Les lignes indentées ou à puce sans heure deviennent les notes de la tâche précédente,
/// les lignes sans heure ni puce deviennent des titres de section.
/// </summary>
public static partial class PlanParser
{
    // Une heure : « 9h », « 9h30 », « 09:30 », « 2pm », « 9:30 am ». Il faut un marqueur (h, :, am/pm) :
    // un nombre seul (« 5 minutes ») n'est pas une heure.
    private const string AmPm = @"[aApP]\.?[mM]\.?(?![a-zA-Z])";
    private const string T1 = @"(?<h1>[01]?\d|2[0-4])\s*(?:(?:[hH]\s*(?<m1>[0-5]\d)?|:(?<m1>[0-5]\d))(?:\s*(?<ap1>" + AmPm + @"))?|(?<ap1>" + AmPm + @"))";
    private const string T2 = @"(?<h2>[01]?\d|2[0-4])\s*(?:(?:[hH]\s*(?<m2>[0-5]\d)?|:(?<m2>[0-5]\d))(?:\s*(?<ap2>" + AmPm + @"))?|(?<ap2>" + AmPm + @"))";

    [GeneratedRegex(@"^(?:(?:de|dès|à|a|from|von|desde|de las|от|с|dari)\s+)?" + T1 +
                    @"(?:\s*(?:-|–|—|à|a|->|→|/|~|jusqu'à|to|until|bis|hasta|até|до|sampai)\s*" + T2 + @")?" +
                    @"\s*[:\-–—,.|]?\s*(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeLine();

    [GeneratedRegex(@"^\s*(?:[*\-•·▪►>]+|\d+[.)])\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\s*\((?<inner>[^()]*)\)\s*\.?\s*$")]
    private static partial Regex TrailingParen();

    [GeneratedRegex(@"^\s*~?\s*\d+\s*(?:h\s*\d*|min|minutes?|heures?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationLike();

    // Pauses et repas, en plusieurs langues (les écritures non latines n'ont pas de « \b » fiable).
    [GeneratedRegex(@"\b(pause|break|repas|d[ée]jeuner|d[îi]ner|go[ûu]ter|d[ée]tente|sieste|souper|collation|repos|lunch|dinner|breakfast|brunch|snack|rest|nap|coffee|pausa|descanso|almuerzo|comida|cena|merienda|almo[çc]o|jantar|lanche|mittag(essen)?|abendessen|fr[üu]hst[üu]ck|istirahat|makan)\b|перерыв|обед|ужин|завтрак|отдых|休息|午饭|午餐|晚饭|晚餐|早餐|休憩|昼食|夕食|昼ご飯|朝食|विश्राम|ब्रेक|भोजन|खाना|नाश्ता|استراحة|غداء|عشاء|فطور|বিরতি|খাবার|দুপুরের", RegexOptions.IgnoreCase)]
    private static partial Regex BreakWords();

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex MarkdownNoise();

    public static List<PlanTask> Parse(string text, DateOnly date)
    {
        var tasks = new List<PlanTask>();
        var explicitEnd = new HashSet<PlanTask>();
        string? section = null;
        PlanTask? last = null;

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var line = MarkdownNoise().Replace(raw, "");
            bool indented = line.Length > 0 && (line[0] == ' ' || line[0] == '\t') &&
                            line.Length - line.TrimStart().Length >= 2;
            bool hasBullet = Bullet().IsMatch(line);
            var content = Bullet().Replace(line, "").Trim().TrimStart('#').Trim();
            if (content.Length == 0) continue;

            var m = TimeLine().Match(content);
            if (m.Success && !(indented && last != null && !LooksLikeTask(m)))
            {
                var start = ToTime(m.Groups["h1"].Value, m.Groups["m1"].Value, m.Groups["ap1"].Value);
                var task = new PlanTask { Date = date, Start = start, End = start + TimeSpan.FromHours(1), Section = section };
                if (m.Groups["h2"].Success)
                {
                    task.End = ToTime(m.Groups["h2"].Value, m.Groups["m2"].Value, m.Groups["ap2"].Value);
                    explicitEnd.Add(task);
                }
                SplitTitle(ExtractMarks(ExtractRhythm(m.Groups["rest"].Value.Trim(), task), task), task);
                task.IsBreak = BreakWords().IsMatch(task.Title);
                tasks.Add(task);
                last = task;
            }
            else if (last != null && (indented || hasBullet))
            {
                last.Notes = string.IsNullOrEmpty(last.Notes) ? content : last.Notes + "\n" + content;
            }
            else
            {
                // Ligne sans heure et sans puce : titre de section.
                var paren = TrailingParen().Match(content);
                section = (paren.Success && DurationLike().IsMatch(paren.Groups["inner"].Value)
                    ? content[..paren.Index] : content).Trim();
                last = null;
            }
        }

        // Sans heure de fin : la tâche dure jusqu'au début de la suivante (sinon 1h).
        for (int i = 0; i < tasks.Count - 1; i++)
            if (!explicitEnd.Contains(tasks[i]) && tasks[i + 1].Start > tasks[i].Start)
                tasks[i].End = tasks[i + 1].Start;

        foreach (var t in tasks)
        {
            if (t.End.TotalHours >= 24) t.End = t.End - TimeSpan.FromDays(1);
            ExtractLinks(t);
        }

        return tasks;
    }

    [GeneratedRegex(@"^\s*(?<h>[01]?\d|2[0-3])\s*(?:[hH:.]\s*(?<m>[0-5]\d)?)?\s*(?<ap>" + AmPm + @")?\s*$")]
    private static partial Regex SingleTime();

    /// <summary>Accepte « 9 », « 9h », « 9h30 », « 09:30 », « 9.30 », « 2pm », « 9:30 am ».</summary>
    public static bool TryParseTime(string text, out TimeSpan time)
    {
        var m = SingleTime().Match(text);
        time = m.Success ? ToTime(m.Groups["h"].Value, m.Groups["m"].Value, m.Groups["ap"].Value) : default;
        return m.Success;
    }

    [GeneratedRegex(@"(https?://|www\.)[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    /// <summary>
    /// « 14h Faire mon CV https://canva.com » : le lien devient un bouton de la tâche et sort du titre.
    /// Les lignes de notes qui ne contiennent qu'un lien sont retirées des notes.
    /// </summary>
    private static void ExtractLinks(PlanTask t)
    {
        void Add(string raw)
        {
            var url = raw.TrimEnd('.', ',', ';', ':');
            if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            if (t.Links.Any(l => l.Target == url)) return;
            t.Links.Add(new TaskLink { Title = Launcher.NameFor(url), Target = url });
        }

        foreach (Match m in Url().Matches(t.Title)) Add(m.Value);
        var title = Url().Replace(t.Title, "").Trim().TrimEnd(':', '-', '–', '(').Trim();
        if (title.Length > 0) t.Title = title;

        if (t.Notes.Length == 0) return;
        var kept = new List<string>();
        foreach (var line in t.Notes.Split('\n'))
        {
            foreach (Match m in Url().Matches(line)) Add(m.Value);
            if (Url().Replace(line, "").Trim(' ', ':', '-', '–').Length > 0) kept.Add(line);
        }
        t.Notes = string.Join("\n", kept);
    }

    // « #pomodoro », « #pomo », « 🍅 », « #50/10 », « (25/5) » : la tâche est découpée en sessions et pauses.
    [GeneratedRegex(@"\s*(?:#(?<w>\d{1,3})\s*/\s*(?<p>\d{1,3})\b|\((?<w>\d{1,3})\s*/\s*(?<p>\d{1,3})\)|#pomo(?:doro)?\b|🍅)", RegexOptions.IgnoreCase)]
    private static partial Regex RhythmTag();

    private static string ExtractRhythm(string rest, PlanTask task)
    {
        var m = RhythmTag().Match(rest);
        if (!m.Success) return rest;
        task.Rhythm = m.Groups["w"].Success ? Rhythm.Parse($"{m.Groups["w"].Value}/{m.Groups["p"].Value}")?.ToString() : Rhythm.Presets[0].Code;
        return RhythmTag().Replace(rest, "").Trim();
    }

    // Un pictogramme en tête de ligne : « 9h 🏃 Courir ».
    [GeneratedRegex(@"^(?<e>[\p{Cs}\p{So}]{1,4}(?:️)?)\s+")]
    private static partial Regex LeadingEmoji();

    /// <summary>
    /// « 9h 🏃 Courir » : le pictogramme sort du titre et devient l'icône de la tâche.
    /// C'est ce qu'on écrit naturellement dans un programme collé, autant s'en servir.
    /// </summary>
    private static string ExtractMarks(string rest, PlanTask task)
    {
        var emoji = LeadingEmoji().Match(rest);
        if (!emoji.Success) return rest;
        task.Emoji = emoji.Groups["e"].Value;
        return rest[emoji.Length..];
    }

    private static bool LooksLikeTask(Match m) => m.Groups["rest"].Value.Trim().Length > 0;

    private static TimeSpan ToTime(string h, string m, string ampm = "")
    {
        int hour = int.Parse(h);
        if (ampm.Length > 0)
        {
            bool pm = char.ToLowerInvariant(ampm[0]) == 'p';
            hour = hour % 12 + (pm ? 12 : 0);
        }
        return new TimeSpan(hour, string.IsNullOrEmpty(m) ? 0 : int.Parse(m), 0);
    }

    /// <summary>
    /// « Pause obligatoire (Levez-vous, étirez-vous). » → titre « Pause obligatoire », note « Levez-vous, étirez-vous ».
    /// « Échauffement (30 min) » → « Échauffement ».
    /// « Réveil et repas. Ne commencez pas le ventre vide. » → titre + note.
    /// </summary>
    private static void SplitTitle(string rest, PlanTask task)
    {
        var notes = new List<string>();
        var title = rest;

        var paren = TrailingParen().Match(title);
        if (paren.Success && paren.Index > 0)
        {
            var inner = paren.Groups["inner"].Value.Trim();
            title = title[..paren.Index];
            if (!DurationLike().IsMatch(inner)) notes.Add(inner);
        }

        var dot = title.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && dot < title.Length - 2)
        {
            notes.Insert(0, title[(dot + 2)..].Trim());
            title = title[..dot];
        }

        task.Title = title.Trim().TrimEnd('.', ':', ' ');
        if (task.Title.Length == 0) task.Title = L.T("plan.task");
        task.Notes = string.Join("\n", notes);
    }
}
