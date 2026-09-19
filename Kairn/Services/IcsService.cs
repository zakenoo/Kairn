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

    public static List<PlanTask> Import(string path)
    {
        var result = new List<PlanTask>();
        var lines = Unfold(File.ReadAllText(path));
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
        }
        return result;
    }

    private static PlanTask? ToTask(Dictionary<string, string> ev)
    {
        if (!ev.TryGetValue("DTSTART", out var s) || !TryParseDate(s, out var start, out bool allDay)) return null;
        if (allDay) return null; // les journées entières n'ont pas de plage horaire
        var end = ev.TryGetValue("DTEND", out var e) && TryParseDate(e, out var d, out _) ? d : start.AddHours(1);
        return new PlanTask
        {
            Id = ev.TryGetValue("UID", out var uid) ? StableId(uid) : Guid.NewGuid().ToString("N"),
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
