using System.Globalization;
using System.IO;
using System.Text;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Import / export au format iCalendar (.ics), lisible par Apple Calendrier, Google Agenda, Outlook…
/// Les heures sont écrites en « heure locale flottante » : pas de fuseau, pas de serveur, rien ne sort du PC.
/// </summary>
public static class IcsService
{
    public static void Export(string path, IEnumerable<PlanTask> tasks)
    {
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Kairn//FR\r\nCALSCALE:GREGORIAN\r\n");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        foreach (var t in tasks.Where(t => !t.Floating).OrderBy(t => t.Date).ThenBy(t => t.Start))
        {
            var start = t.Date.ToDateTime(TimeOnly.MinValue) + t.Start;
            var end = start + t.Duration;
            sb.Append("BEGIN:VEVENT\r\n");
            sb.Append($"UID:{t.Id}@kairn\r\n");
            sb.Append($"DTSTAMP:{stamp}\r\n");
            sb.Append($"DTSTART:{start:yyyyMMdd'T'HHmmss}\r\n");
            sb.Append($"DTEND:{end:yyyyMMdd'T'HHmmss}\r\n");
            sb.Append(Fold("SUMMARY:" + Escape(t.Title)));
            if (t.HasNotes) sb.Append(Fold("DESCRIPTION:" + Escape(t.Notes)));
            var cat = Storage.CategoryById(t.CategoryId);
            if (cat != null) sb.Append(Fold("CATEGORIES:" + Escape(cat.Name)));
            sb.Append("END:VEVENT\r\n");
        }
        sb.Append("END:VCALENDAR\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static List<PlanTask> Import(string path) => ParseText(File.ReadAllText(path));

    /// <summary>
    /// Lit un flux iCalendar, d'où qu'il vienne : un fichier choisi à la main, ou la réponse d'un serveur CalDAV.
    /// Les journées entières sont ignorées : elles n'occupent pas de créneau et ne diraient rien de la disponibilité.
    /// </summary>
    public static List<PlanTask> ParseText(string text)
    {
        var result = new List<PlanTask>();
        var lines = Unfold(text);
        Dictionary<string, string>? ev = null;

        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT") { ev = new(); continue; }
            if (line == "END:VEVENT" && ev != null)
            {
                var task = ToTask(ev);
                if (task != null) result.Add(task);
                ev = null;
                continue;
            }
            if (ev == null) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Split(';')[0].ToUpperInvariant();
            ev.TryAdd(name, line[(colon + 1)..]);
            // Le vrai identifiant de l'occurrence d'un événement récurrent : UID + date de l'occurrence.
            if (name == "RECURRENCE-ID") ev["__RECURRENCE"] = line[(colon + 1)..];
        }
        return result;
    }

    /// <summary>Écrit un seul événement, prêt à être déposé sur un serveur CalDAV.</summary>
    public static string EventText(PlanTask t, string uid)
    {
        var start = t.Date.ToDateTime(TimeOnly.MinValue) + t.Start;
        var end = start + t.Duration;
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Kairn//FR\r\nCALSCALE:GREGORIAN\r\n");
        sb.Append("BEGIN:VEVENT\r\n");
        sb.Append($"UID:{uid}\r\n");
        sb.Append($"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}\r\n");
        // En UTC : le serveur et le téléphone n'ont aucune raison d'être dans le même fuseau que ce PC.
        sb.Append($"DTSTART:{start.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}\r\n");
        sb.Append($"DTEND:{end.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}\r\n");
        sb.Append(Fold("SUMMARY:" + Escape((t.HasEmoji ? t.Emoji + " " : "") + t.Title)));
        var body = t.Notes;
        if (t.HasSteps) body = (body.Length > 0 ? body + "\n\n" : "") + string.Join("\n", t.Steps.Select(s => (s.Done ? "☑ " : "☐ ") + s.Title));
        if (body.Length > 0) sb.Append(Fold("DESCRIPTION:" + Escape(body)));
        sb.Append("END:VEVENT\r\nEND:VCALENDAR\r\n");
        return sb.ToString();
    }

    private static PlanTask? ToTask(Dictionary<string, string> ev)
    {
        if (!ev.TryGetValue("DTSTART", out var s) || !TryParseDate(s, out var start, out bool allDay)) return null;
        if (allDay) return null; // les journées entières n'ont pas de plage horaire
        var end = ev.TryGetValue("DTEND", out var e) && TryParseDate(e, out var d, out _) ? d : start.AddHours(1);
        var uid = ev.GetValueOrDefault("UID", Guid.NewGuid().ToString("N"));
        // Un événement réexporté par Kairn retrouve son identifiant d'origine : réimporter ne duplique rien.
        // Sinon, les occurrences d'un événement récurrent partagent le même UID : on y ajoute leur date.
        var key = uid.EndsWith("@kairn") ? uid
                : uid + (ev.TryGetValue("__RECURRENCE", out var r) ? "#" + r : "#" + start.ToString("yyyyMMddHHmm"));
        return new PlanTask
        {
            Id = StableId(key),
            Date = DateOnly.FromDateTime(start),
            Start = start.TimeOfDay,
            End = end.TimeOfDay,
            Title = Unescape(ev.GetValueOrDefault("SUMMARY", L.T("ics.event"))),
            Notes = Unescape(ev.GetValueOrDefault("DESCRIPTION", "")),
        };
    }

    private static string StableId(string uid) =>
        uid.EndsWith("@kairn") ? uid[..^6] : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(uid)))[..32].ToLowerInvariant();

    private static bool TryParseDate(string v, out DateTime dt, out bool allDay)
    {
        allDay = v.Length == 8;
        bool utc = v.EndsWith('Z');
        var fmt = allDay ? "yyyyMMdd" : "yyyyMMdd'T'HHmmss";
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return false;
        if (utc) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        return true;
    }

    private static List<string> Unfold(string text)
    {
        var list = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && list.Count > 0) list[^1] += raw[1..];
            else if (raw.Length > 0) list.Add(raw);
        }
        return list;
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

    private static string Unescape(string s) =>
        s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    /// <summary>Plie les lignes à 75 octets comme l'exige la norme.</summary>
    private static string Fold(string line)
    {
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (bytes + n > 74) { sb.Append("\r\n "); bytes = 1; }
            sb.Append(rune.ToString());
            bytes += n;
        }
        return sb.Append("\r\n").ToString();
    }
}
