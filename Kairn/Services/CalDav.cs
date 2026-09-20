using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>Un calendrier trouvé sur le serveur : son adresse, son nom affiché, sa couleur.</summary>
public record RemoteCalendar(string Href, string Name, string? Color);

/// <summary>Le serveur a refusé ou n'a pas compris. Le message est déjà lisible par la personne.</summary>
public class CalDavException(string key, string? detail = null) : Exception(detail ?? key)
{
    /// <summary>Clé de traduction du message à afficher.</summary>
    public string Key { get; } = key;
}

/// <summary>
/// Accès aux calendriers iCloud par CalDAV, directement entre ce PC et Apple. Aucun intermédiaire,
/// aucun compte Kairn, et un mot de passe d'application révocable côté Apple à tout moment.
///
/// Les événements récurrents sont développés <b>par le serveur</b> (« expand ») : Kairn n'a pas à
/// interpréter les règles de répétition, donc pas de dépendance et pas d'occurrence inventée.
/// </summary>
public static class CalDav
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace A = "http://apple.com/ns/ical/";

    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HttpMethod Report = new("REPORT");
    private static readonly HttpMethod MkCalendar = new("MKCALENDAR");

    private static HttpClient Client(CalendarAccount a)
    {
        var password = Secret.Unprotect(a.ProtectedPassword ?? "") ?? throw new CalDavException("cal.err.auth");
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{a.User}:{password}")));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Kairn/1.0");
        return http;
    }

    private static async Task<XDocument> SendAsync(HttpClient http, HttpMethod method, string url, string? body, int depth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Depth", depth.ToString());
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        using var res = await http.SendAsync(req, ct);

        var text = await res.Content.ReadAsStringAsync(ct);
        CalDavLog.Line($"{method.Method} {url} -> {(int)res.StatusCode} {res.ReasonPhrase} ({text.Length} o)");

        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new CalDavException("cal.err.auth");
        if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.MultiStatus)
        {
            // Le corps d'erreur dit souvent précisément ce qui cloche côté Apple.
            CalDavLog.Line("  corps : " + Snippet(text));
            throw new CalDavException("cal.err.server", $"{(int)res.StatusCode} {res.ReasonPhrase}");
        }

        try { return XDocument.Parse(text); }
        catch
        {
            CalDavLog.Line("  réponse illisible : " + Snippet(text));
            throw new CalDavException("cal.err.server", "réponse illisible");
        }
    }

    /// <summary>Début d'une réponse, pour le journal : assez pour diagnostiquer, pas assez pour tout recopier.</summary>
    private static string Snippet(string text) =>
        text.Length <= 400 ? text.ReplaceLineEndings(" ") : text[..400].ReplaceLineEndings(" ") + "…";

    /// <summary>Transforme un chemin renvoyé par le serveur (« /123/calendars/ ») en adresse complète.</summary>
    private static string Absolute(string baseUrl, string href) =>
        Uri.TryCreate(new Uri(baseUrl), href, out var abs) ? abs.ToString() : href;

    // ===================== Connexion =====================

    /// <summary>
    /// Vérifie les identifiants et trouve le dossier des calendriers.
    /// Deux allers-retours : l'utilisateur courant, puis son dossier de calendriers.
    /// </summary>
    public static async Task<string> FindHomeAsync(CalendarAccount a, CancellationToken ct)
    {
        using var http = Client(a);

        var principalBody = $"""
            <d:propfind xmlns:d="DAV:"><d:prop><d:current-user-principal/></d:prop></d:propfind>
            """;
        var doc = await SendAsync(http, PropFind, a.BaseUrl, principalBody, 0, ct);
        var principal = doc.Descendants(D + "current-user-principal").Descendants(D + "href").FirstOrDefault()?.Value
            ?? throw new CalDavException("cal.err.auth");

        var homeBody = $"""
            <d:propfind xmlns:d="DAV:" xmlns:c="{C}"><d:prop><c:calendar-home-set/></d:prop></d:propfind>
            """;
        var homeDoc = await SendAsync(http, PropFind, Absolute(a.BaseUrl, principal), homeBody, 0, ct);
        var home = homeDoc.Descendants(C + "calendar-home-set").Descendants(D + "href").FirstOrDefault()?.Value
            ?? throw new CalDavException("cal.err.noHome");

        return Absolute(a.BaseUrl, home);
    }

    /// <summary>Liste les calendriers d'événements (on laisse de côté les rappels, les carnets d'adresses…).</summary>
    public static async Task<List<RemoteCalendar>> ListAsync(CalendarAccount a, CancellationToken ct)
    {
        var home = a.HomeUrl ?? await FindHomeAsync(a, ct);
        using var http = Client(a);
        var body = $"""
            <d:propfind xmlns:d="DAV:" xmlns:c="{C}" xmlns:a="{A}">
              <d:prop>
                <d:resourcetype/><d:displayname/>
                <c:supported-calendar-component-set/>
                <a:calendar-color/>
              </d:prop>
            </d:propfind>
            """;
        var doc = await SendAsync(http, PropFind, home, body, 1, ct);

        var list = new List<RemoteCalendar>();
        foreach (var response in doc.Descendants(D + "response"))
        {
            var href = response.Element(D + "href")?.Value;
            if (href is null) continue;
            var props = response.Descendants(D + "prop").FirstOrDefault();
            if (props is null) continue;
            if (props.Descendants(C + "calendar").FirstOrDefault() is null) continue; // pas un calendrier

            // Seuls les calendriers qui acceptent des événements (VEVENT) nous intéressent.
            var comps = props.Descendants(C + "comp").Select(c => (string?)c.Attribute("name")).ToList();
            if (comps.Count > 0 && !comps.Contains("VEVENT")) continue;

            var name = props.Descendants(D + "displayname").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(name)) name = href.Trim('/').Split('/').LastOrDefault() ?? href;
            list.Add(new RemoteCalendar(Absolute(home, href), name, props.Descendants(A + "calendar-color").FirstOrDefault()?.Value));
        }
        // Savoir quels calendriers existent vraiment évite de chercher un rendez-vous
        // dans celui qui n'est pas coché.
        CalDavLog.Line("calendriers trouvés : " + string.Join(", ", list.Select(c => $"« {c.Name} »")));
        return list;
    }

    // ===================== Lecture =====================

    /// <summary>
    /// Les rendez-vous d'un calendrier entre deux dates. Le serveur développe lui-même les répétitions,
    /// donc « tous les mardis » revient bien comme une suite de mardis, avec ses exceptions.
    /// </summary>
    public static async Task<List<PlanTask>> FetchAsync(CalendarAccount a, string calendarHref, DateOnly from, DateOnly to, CancellationToken ct)
    {
        using var http = Client(a);
        string Utc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");

        string Query(bool expand) => $"""
            <c:calendar-query xmlns:d="DAV:" xmlns:c="{C}">
              <d:prop>
                <d:getetag/>
                <c:calendar-data>{(expand ? $"<c:expand start=\"{Utc(from)}\" end=\"{Utc(to)}\"/>" : "")}</c:calendar-data>
              </d:prop>
              <c:filter>
                <c:comp-filter name="VCALENDAR">
                  <c:comp-filter name="VEVENT">
                    <c:time-range start="{Utc(from)}" end="{Utc(to)}"/>
                  </c:comp-filter>
                </c:comp-filter>
              </c:filter>
            </c:calendar-query>
            """;

        var tasks = await RunQueryAsync(http, calendarHref, Query(expand: true), ct);
        if (tasks.Count > 0) return Within(tasks, from, to);

        // Certains serveurs renvoient une réponse vide quand on leur demande de développer les
        // répétitions. On repose alors la même question sans « expand » : les événements simples
        // reviennent, et une répétition revient au moins une fois au lieu de disparaître.
        CalDavLog.Line("  aucun résultat avec expand, nouvelle tentative sans");
        tasks = await RunQueryAsync(http, calendarHref, Query(expand: false), ct);
        return Within(tasks, from, to);
    }

    private static async Task<List<PlanTask>> RunQueryAsync(HttpClient http, string calendarHref, string body, CancellationToken ct)
    {
        var doc = await SendAsync(http, Report, calendarHref, body, 1, ct);
        var tasks = new List<PlanTask>();
        foreach (var data in doc.Descendants(C + "calendar-data"))
        {
            if (string.IsNullOrWhiteSpace(data.Value)) continue;
            tasks.AddRange(IcsService.ParseText(data.Value));
        }
        if (tasks.Count == 0) CalDavLog.Line("  réponse : " + Snippet(doc.ToString()));
        return tasks;
    }

    /// <summary>
    /// Ne garde que ce qui tombe vraiment dans la fenêtre. Sans « expand », le serveur peut renvoyer
    /// l'événement d'origine d'une répétition, daté d'il y a des années : le placer tel quel serait faux.
    /// </summary>
    private static List<PlanTask> Within(List<PlanTask> tasks, DateOnly from, DateOnly to)
    {
        var kept = tasks.Where(t => t.Date >= from && t.Date <= to).ToList();
        if (kept.Count != tasks.Count) CalDavLog.Line($"  {tasks.Count - kept.Count} événement(s) hors fenêtre, ignorés");
        return kept;
    }

    // ===================== Écriture =====================

    /// <summary>Nom du calendrier que Kairn crée pour y déposer ses propres séances. Il n'écrit nulle part ailleurs.</summary>
    public const string OwnCalendarName = "Kairn";

    /// <summary>
    /// Trouve le calendrier « Kairn », ou le crée s'il n'existe pas.
    /// Rien n'est jamais écrit dans un calendrier existant : les rendez-vous de la personne sont intouchables.
    /// </summary>
    public static async Task<string> EnsureOwnCalendarAsync(CalendarAccount a, CancellationToken ct)
    {
        var existing = (await ListAsync(a, ct)).FirstOrDefault(c => c.Name == OwnCalendarName);
        if (existing != null) return existing.Href;

        var home = a.HomeUrl ?? await FindHomeAsync(a, ct);
        var url = home.TrimEnd('/') + "/kairn-" + Guid.NewGuid().ToString("N")[..8] + "/";
        using var http = Client(a);
        var body = $"""
            <c:mkcalendar xmlns:d="DAV:" xmlns:c="{C}">
              <d:set><d:prop>
                <d:displayname>{OwnCalendarName}</d:displayname>
                <c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set>
              </d:prop></d:set>
            </c:mkcalendar>
            """;
        using var req = new HttpRequestMessage(MkCalendar, url) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new CalDavException("cal.err.create", $"{(int)res.StatusCode} {res.ReasonPhrase}");
        return url;
    }

    /// <summary>Identifiant d'un événement déposé par Kairn. Le suffixe permet de reconnaître les siens à coup sûr.</summary>
    public static string OwnUid(PlanTask t) => t.Id + "@kairn";

    /// <summary>Dépose (ou met à jour) une séance dans le calendrier « Kairn ».</summary>
    public static async Task PutAsync(CalendarAccount a, string calendarHref, PlanTask t, CancellationToken ct)
    {
        using var http = Client(a);
        var uid = OwnUid(t);
        var url = calendarHref.TrimEnd('/') + "/" + Uri.EscapeDataString(uid) + ".ics";
        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(IcsService.EventText(t, uid), Encoding.UTF8)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar") { CharSet = "utf-8" };
        using var res = await http.SendAsync(req, ct);
        CalDavLog.Line($"PUT {uid} -> {(int)res.StatusCode} {res.ReasonPhrase}");
        if (!res.IsSuccessStatusCode)
        {
            CalDavLog.Line("  corps : " + Snippet(await res.Content.ReadAsStringAsync(ct)));
            throw new CalDavException("cal.err.write", $"{(int)res.StatusCode} {res.ReasonPhrase}");
        }
    }

    /// <summary>Retire une séance que Kairn avait déposée (et elle seule).</summary>
    public static async Task DeleteAsync(CalendarAccount a, string calendarHref, string uid, CancellationToken ct)
    {
        using var http = Client(a);
        var url = calendarHref.TrimEnd('/') + "/" + Uri.EscapeDataString(uid) + ".ics";
        using var res = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url), ct);
        // Déjà disparu côté serveur : c'est le résultat voulu, pas une erreur.
        if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.NotFound)
            throw new CalDavException("cal.err.write", $"{(int)res.StatusCode} {res.ReasonPhrase}");
    }
}
