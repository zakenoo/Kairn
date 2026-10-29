using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Ce que la capture rapide comprend en plus d'un programme collé :
///   « demain 14h Dentiste », « Appeler maman vendredi », « Rapport dans 3 jours », « Courir à 7h30 »,
///   « chaque lundi 9h Sport », « tous les jours 8h 💊 Médicaments », « en semaine 18h Ranger ».
/// Tout est fait sur le PC, sans IA : des mots courants, en français, en anglais, en espagnol, en allemand et en portugais,
/// plus les noms de jours de la langue de l'interface. Ce qui n'est pas compris reste dans le titre, rien ne se perd.
/// </summary>
public static partial class QuickParse
{
    /// <param name="Repeat">Jours de la routine à créer, ou null pour une tâche ponctuelle.</param>
    public record Result(PlanTask Task, DayOfWeek[]? Repeat);

    private static readonly string[][] DayWords =
    [
        // dimanche à samedi, dans l'ordre de DayOfWeek
        ["dimanche", "sunday", "domingo", "sonntag"],
        ["lundi", "monday", "lunes", "montag", "segunda", "segunda-feira"],
        ["mardi", "tuesday", "martes", "dienstag", "terca", "terca-feira"],
        ["mercredi", "wednesday", "miercoles", "mittwoch", "quarta", "quarta-feira"],
        ["jeudi", "thursday", "jueves", "donnerstag", "quinta", "quinta-feira"],
        ["vendredi", "friday", "viernes", "freitag", "sexta", "sexta-feira"],
        ["samedi", "saturday", "sabado", "samstag"],
    ];

    private static Dictionary<string, DayOfWeek>? _days;
    private static string? _daysCulture;

    /// <summary>Noms de jours connus (repliés : minuscules, sans accents), avec ceux de la langue de l'interface.</summary>
    private static Dictionary<string, DayOfWeek> Days()
    {
        if (_days != null && _daysCulture == L.Culture.Name) return _days;
        var map = new Dictionary<string, DayOfWeek>();
        for (int d = 0; d < 7; d++)
        {
            foreach (var w in DayWords[d]) map[w] = (DayOfWeek)d;
            var name = Fold(L.Culture.DateTimeFormat.GetDayName((DayOfWeek)d));
            if (name.Length > 1 && !name.Any(char.IsWhiteSpace)) map.TryAdd(name, (DayOfWeek)d);
        }
        _daysCulture = L.Culture.Name;
        return _days = map;
    }

    private static string DayAlt() =>
        string.Join("|", Days().Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));

    /// <summary>Minuscules sans accents, même longueur que le texte d'origine : les positions trouvées restent valables.</summary>
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            // Une moitié d'emoji ne se normalise pas seule : elle est gardée telle quelle.
            if (char.IsSurrogate(c)) { sb.Append(c); continue; }
            var d = c.ToString().Normalize(NormalizationForm.FormD);
            sb.Append(char.ToLowerInvariant(d.Length > 0 && char.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark ? d[0] : c));
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(?<![\p{L}\d])(?:tous les jours|chaque jour|every ?day|daily|todos los dias|cada dia|jeden tag|taglich|todos os dias)(?![\p{L}])")]
    private static partial Regex EveryDayWords();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:en semaine|du lundi au vendredi|les jours de semaine|every weekday|on weekdays|weekdays|entre semana|de lunes a viernes|werktags|dias uteis|durante a semana)(?![\p{L}])")]
    private static partial Regex WeekdayWords();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:apres-demain|apres demain|the day after tomorrow|day after tomorrow|pasado manana|ubermorgen|depois de amanha)(?![\p{L}])")]
    private static partial Regex InTwoDays();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:demain|tomorrow|manana|morgen|amanha)(?![\p{L}])")]
    private static partial Regex Tomorrow();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:aujourd'hui|aujourd’hui|aujourdhui|ce soir|ce matin|cet apres-midi|today|tonight|this evening|hoy|heute|hoje)(?![\p{L}])")]
    private static partial Regex TodayWords();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:dans|in|en|em)\s+(?<n>\d{1,2})\s+(?<u>jours?|days?|dias?|tagen?|semaines?|weeks?|semanas?|wochen?)(?![\p{L}])")]
    private static partial Regex InDays();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:(?:le|on|el|am|em|pour|for)\s+)?(?<d>[0-3]?\d)[/.](?<m>[01]?\d)(?:[/.](?<y>\d{2,4}))?(?![\d:h])")]
    private static partial Regex NumericDate();

    // Une heure n'importe où : « à 14h », « at 2pm », « vers 9h30 », « 18:00 », avec une fin facultative (« 14h-15h30 »).
    private const string Clock = @"(?:(?:[01]?\d|2[0-3])\s*(?:h(?:\s*[0-5]\d)?|:[0-5]\d)(?:\s*[ap]\.?m\.?)?|(?:1[0-2]|0?[1-9])\s*[ap]\.?m\.?)(?![\p{L}\d])";

    [GeneratedRegex(@"(?<![\p{L}\d:])(?:(?<p>a|at|vers|around|um|as|a las|de|from)\s+)?(?<t>" + Clock + @"(?:\s*(?:-|–|a|to|bis|hasta|ate)\s*" + Clock + @")?)")]
    private static partial Regex AnyTime();

    [GeneratedRegex(@"^\s*" + Clock)]
    private static partial Regex StartsWithTime();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    /// <summary>Lit une ligne de capture. Null si elle ne contient rien qui ressemble à un titre.</summary>
    public static Result? Parse(string text, DateTime now) => Parse(text, now, allowRepeat: true);

    private static Result? Parse(string text, DateTime now, bool allowRepeat)
    {
        var today = DateOnly.FromDateTime(now);
        var rest = text.Trim();
        if (rest.Length == 0) return null;

        DayOfWeek[]? repeat = null;
        DateOnly? date = null;

        if (allowRepeat)
        {
            if (Take(EveryDayWords(), ref rest) != null) repeat = Routines.EveryDay;
            else if (Take(WeekdayWords(), ref rest) != null) repeat = Routines.Weekdays;
            else
            {
                var days = Days();
                var every = new Regex(@"(?<![\p{L}\d])(?:chaque|tous les|toutes les|every|each|cada|todos los|todas las|jeden|todas as|todos os)\s+(?<list>(?:" + DayAlt() +
                                      @")s?(?:\s*(?:,|et|and|y|und|e)\s*(?:" + DayAlt() + @")s?)*)(?![\p{L}])");
                if (Take(every, ref rest) is { } m)
                {
                    var found = Regex.Matches(m.Groups["list"].Value, DayAlt())
                        .Select(x => days[x.Value]).Distinct().ToArray();
                    if (found.Length > 0) repeat = found;
                }
            }
        }

        if (repeat is null)
        {
            if (Take(InTwoDays(), ref rest) != null) date = today.AddDays(2);
            else if (Take(Tomorrow(), ref rest) != null) date = today.AddDays(1);
            else if (Take(TodayWords(), ref rest) != null) date = today;
            else if (Take(InDays(), ref rest) is { } inDays)
            {
                int n = int.Parse(inDays.Groups["n"].Value);
                var unit = inDays.Groups["u"].Value;
                bool weeks = unit.StartsWith("sem") || unit.StartsWith("week") || unit.StartsWith("woch");
                date = today.AddDays(weeks ? n * 7 : n);
            }
            else if (TakeNumericDate(ref rest, today) is { } d) date = d;
            else if (TakeWeekday(ref rest, today) is { } w) date = w;
        }

        // « Dentiste à 14h » : l'heure passe devant, là où le lecteur de programmes l'attend.
        if (!StartsWithTime().IsMatch(Fold(rest)) && AnyTime().Match(Fold(rest)) is { Success: true } t && !LooksLikeDuration(t))
        {
            var time = rest.Substring(t.Groups["t"].Index, t.Groups["t"].Length);
            rest = time + " " + rest.Remove(t.Index, t.Length);
        }
        rest = Spaces().Replace(rest, " ").Trim(' ', ',', ';', '-', '–', ':');
        if (rest.Length == 0) return null;

        var day = date ?? today;
        var parsed = PlanParser.Parse(rest, day);
        PlanTask task;
        if (parsed.Count == 1 && parsed[0].Title.Length > 0 && !rest.Contains('\n')) task = parsed[0];
        else
        {
            // Pas d'heure : la tâche attend dans « À reprendre » du jour dit (aujourd'hui si rien n'est dit).
            var start = new TimeSpan(now.Hour, 0, 0);
            task = new PlanTask { Date = day, Start = start, End = start + TimeSpan.FromMinutes(30), Title = rest, Floating = true };
        }

        if (repeat != null)
        {
            // Une routine a besoin d'une heure : sans elle, on relit la ligne comme une tâche ordinaire.
            if (task.Floating) return Parse(text, now, allowRepeat: false);
            var from = task.Start <= now.TimeOfDay ? today.AddDays(1) : today;
            task.Date = Routines.FirstDay(repeat, from);
        }
        return new Result(task, repeat);
    }

    /// <summary>« Lire 2h » parle d'une durée, pas de deux heures du matin : sans « à » ni minutes, une petite heure n'est pas un horaire.</summary>
    private static bool LooksLikeDuration(Match t)
    {
        if (t.Groups["p"].Success) return false;
        var v = t.Groups["t"].Value;
        if (v.Contains(':') || v.Contains('m') || Regex.IsMatch(v, @"h\s*\d")) return false;
        return int.TryParse(Regex.Match(v, @"\d+").Value, out var h) && h < 6;
    }

    /// <summary>Retire du texte la première correspondance (cherchée sans accents ni majuscules) et la renvoie.</summary>
    private static Match? Take(Regex re, ref string rest)
    {
        var m = re.Match(Fold(rest));
        if (!m.Success) return null;
        rest = rest.Remove(m.Index, m.Length);
        return m;
    }

    private static DateOnly? TakeNumericDate(ref string rest, DateOnly today)
    {
        var m = NumericDate().Match(Fold(rest));
        if (!m.Success) return null;
        int a = int.Parse(m.Groups["d"].Value), b = int.Parse(m.Groups["m"].Value);
        // 11/05 : jour puis mois, sauf dans les langues qui écrivent le mois d'abord.
        bool monthFirst = L.Culture.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith('M');
        var (dd, mm) = monthFirst ? (b, a) : (a, b);
        int yy = today.Year;
        if (m.Groups["y"].Success) { yy = int.Parse(m.Groups["y"].Value); if (yy < 100) yy += 2000; }
        if (mm is < 1 or > 12 || dd < 1 || dd > DateTime.DaysInMonth(yy, mm)) return null;
        var d = new DateOnly(yy, mm, dd);
        if (!m.Groups["y"].Success && d < today) d = d.AddYears(1); // « le 3/01 » en décembre : janvier qui vient
        rest = rest.Remove(m.Index, m.Length);
        return d;
    }

    private static DateOnly? TakeWeekday(ref string rest, DateOnly today)
    {
        var re = new Regex(@"(?<![\p{L}\d])(?:(?<next>next|proximo|proxima|nachsten|naechsten)\s+|(?:ce|le|this|este|esta|el|am|on)\s+)?(?<d>" + DayAlt() +
                           @")(?:\s+(?<next2>prochain|qui vient|que viene))?(?![\p{L}])");
        var m = re.Match(Fold(rest));
        if (!m.Success) return null;
        var target = Days()[m.Groups["d"].Value];
        int delta = ((int)target - (int)today.DayOfWeek + 7) % 7;
        if (delta == 0 && (m.Groups["next"].Success || m.Groups["next2"].Success)) delta = 7;
        rest = rest.Remove(m.Index, m.Length);
        return today.AddDays(delta);
    }
}
