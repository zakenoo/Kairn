using System.Windows.Threading;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Fait le va-et-vient avec le calendrier iCloud relié, dans les deux sens :
///   — les rendez-vous de la personne descendent dans Kairn, en lecture seule, et occupent leur créneau
///     (l'assistant ne placera donc jamais une séance par-dessus un vrai rendez-vous) ;
///   — les tâches planifiées de Kairn remontent dans un calendrier « Kairn » créé pour ça, visible
///     sur l'iPhone et le Mac.
///
/// Apple ne prévient pas les applications tierces quand quelque chose change : on redemande
/// toutes les dix minutes, au retour sur la fenêtre (au plus toutes les trois minutes), et quand on clique sur « Rafraîchir ».
/// Rien ne part et rien n'arrive tant que la personne n'a pas relié son compte elle-même.
/// </summary>
public static class CalendarSync
{
    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(10) };
    private static CancellationTokenSource? _running;

    /// <summary>Fenêtre synchronisée : de quoi voir la semaine passée et préparer les deux mois qui viennent.</summary>
    private const int DaysBefore = 7, DaysAfter = 60;

    /// <summary>Levé quand quelque chose a changé (les vues se rafraîchissent), ou qu'une erreur est à montrer.</summary>
    public static event Action? Changed;

    /// <summary>Dernier message d'état, déjà traduit. Vide si tout va bien.</summary>
    public static string Status { get; private set; } = "";
    public static bool Busy { get; private set; }
    /// <summary>Début de la dernière synchro, réussie ou non.</summary>
    public static DateTime LastAttempt { get; private set; }

    private static CalendarAccount? Account => Storage.Settings.Calendar;
    public static bool Linked => Account is { User.Length: > 0, HomeUrl.Length: > 0 };

    /// <summary>Synchro toutes les dix minutes. <paramref name="delay"/> : attente avant la première (ouverture de session).</summary>
    public static void Start(TimeSpan delay)
    {
        Timer.Tick += (_, _) => _ = SyncAsync();
        Timer.Start();
        if (!Linked) return;
        if (delay <= TimeSpan.Zero) { _ = SyncAsync(); return; }
        var first = new DispatcherTimer { Interval = delay };
        first.Tick += (_, _) => { first.Stop(); _ = SyncAsync(); };
        first.Start();
    }

    public static void Stop() => Timer.Stop();

    /// <summary>Synchro au retour sur la fenêtre, sauf si la précédente date de moins de <paramref name="minimum"/>.</summary>
    public static void SyncIfIdleFor(TimeSpan minimum)
    {
        if (Linked && DateTime.Now - LastAttempt >= minimum) _ = SyncAsync();
    }

    /// <summary>Coupe le lien : les rendez-vous importés disparaissent de Kairn, rien n'est touché côté Apple.</summary>
    public static void Unlink()
    {
        Storage.Settings.Calendar = null;
        Storage.SaveSettings();
        CalDav.Forget();
        _names = null;
        Storage.Data.Tasks.RemoveAll(t => t.IsExternal);
        Storage.Save();
        Status = "";
        Changed?.Invoke();
    }

    public static async Task SyncAsync()
    {
        if (Busy || Account is not { } a || !Linked) return;
        LastAttempt = DateTime.Now;
        // Les dix minutes repartent d'ici : inutile de recommencer juste après une synchro faite au retour sur la fenêtre.
        Timer.Stop();
        Timer.Start();
        _running?.Cancel();
        _running = new CancellationTokenSource();
        var ct = _running.Token;
        Busy = true;
        Status = "";
        Changed?.Invoke();

        // Les deux sens sont indépendants : si la lecture échoue, l'envoi doit quand même partir.
        // Les mélanger revenait à casser les deux dès que l'un des deux avait un problème.
        var problems = new List<string>();
        CalDavLog.Line($"--- sync: read={a.Read.Count} calendar(s), push={(a.Push ? "on" : "off")}");

        try { await PullAsync(a, ct); }
        catch (OperationCanceledException) { }
        catch (Exception e) { problems.Add(Describe(e, "pull")); }

        if (a.Push && a.WriteHref is { Length: > 0 })
        {
            try { await PushAsync(a, ct); }
            catch (OperationCanceledException) { }
            catch (Exception e) { problems.Add(Describe(e, "push")); }
        }

        // Gardé en mémoire pour les Réglages : le fichier n'est plus réécrit toutes les dix minutes pour cette seule heure.
        a.LastSync = DateTime.Now;
        Status = problems.Count == 0 ? "" : string.Join(" ", problems.Distinct());
        Busy = false;
        Changed?.Invoke();
    }

    private static string Describe(Exception e, string step)
    {
        CalDavLog.Line($"{step} FAILED: {e.GetType().Name}: {e.Message}");
        return e is CalDavException c ? L.T(c.Key) : L.T("cal.err.network");
    }

    // ===================== iCloud → Kairn =====================

    /// <summary>
    /// Redescend les rendez-vous. La fenêtre est remplacée d'un bloc plutôt que fusionnée :
    /// ce que dit iCloud fait foi, un rendez-vous annulé là-bas disparaît ici, et il n'y a pas de conflit possible.
    /// </summary>
    private static async Task PullAsync(CalendarAccount a, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-DaysBefore);
        var to = today.AddDays(DaysAfter);

        // Aucun calendrier coché : il n'y a rien à descendre, et ce n'est pas une panne.
        // On le dit quand même, parce que « rien ne s'affiche » sans explication ressemble à un bug.
        if (a.Read.Count == 0)
        {
            CalDavLog.Line("pull : aucun calendrier coché dans les réglages, rien à importer");
            return;
        }

        var names = await NamesAsync(a, ct);
        var fetched = new List<PlanTask>();
        foreach (var href in a.Read.ToList())
        {
            ct.ThrowIfCancellationRequested();
            // Le calendrier « Kairn » ne redescend pas : ce sont nos propres tâches, on les a déjà.
            if (href == a.WriteHref) continue;
            var events = await CalDav.FetchAsync(a, href, from, to, ct);
            CalDavLog.Line($"pull : {events.Count} rendez-vous depuis « {names.GetValueOrDefault(href) ?? href} »");
            foreach (var t in events)
            {
                t.ExternalId = t.Id;
                t.ExternalCalendar = names.GetValueOrDefault(href);
                fetched.Add(t);
            }
        }

        // Rien n'a bougé chez Apple : ni les tâches ni le fichier ne sont touchés (avant, tout était réécrit toutes les dix minutes).
        var current = Storage.Data.Tasks.Where(t => t.IsExternal && t.Date >= from && t.Date <= to).ToList();
        if (current.Count == fetched.Count && current.Select(Key).ToHashSet().SetEquals(fetched.Select(Key)))
        {
            CalDavLog.Line("pull : rien de nouveau");
            return;
        }

        Storage.Data.Tasks.RemoveAll(t => t.IsExternal && t.Date >= from && t.Date <= to);
        // Un rendez-vous qui tomberait exactement sur une tâche de Kairn ne l'écrase pas : les deux coexistent.
        Storage.Data.Tasks.AddRange(fetched);
        Storage.Save();
    }

    /// <summary>Tout ce qui, s'il change, doit changer aussi dans Kairn.</summary>
    private static string Key(PlanTask t) =>
        string.Join('\u001f', t.ExternalId, t.ExternalCalendar, t.Date.DayNumber, t.Start.Ticks, t.End.Ticks, t.Title, t.Notes);

    private static Dictionary<string, string>? _names;
    private static DateTime _namesAt;

    /// <summary>
    /// Noms des calendriers (« Perso », « Travail »…), redemandés toutes les six heures ou s'il en manque un.
    /// Avant, la liste complète était redemandée à chaque synchro.
    /// </summary>
    private static async Task<Dictionary<string, string>> NamesAsync(CalendarAccount a, CancellationToken ct)
    {
        if (_names is null || DateTime.Now - _namesAt > TimeSpan.FromHours(6) || a.Read.Any(h => !_names.ContainsKey(h)))
            Remember(await CalDav.ListAsync(a, ct));
        return _names!;
    }

    /// <summary>Retient les calendriers trouvés (les Réglages l'appellent aussi quand ils redemandent la liste).</summary>
    public static void Remember(IEnumerable<RemoteCalendar> calendars)
    {
        var names = new Dictionary<string, string>();
        foreach (var c in calendars) names[c.Href] = c.Name;
        _names = names;
        _namesAt = DateTime.Now;
    }

    // ===================== Kairn → iCloud =====================

    /// <summary>
    /// Fait remonter les tâches planifiées, et seulement dans le calendrier « Kairn ».
    /// Ce qui a disparu côté Kairn est retiré là-bas ; rien d'autre n'est jamais touché.
    /// Une séance inchangée depuis son dernier envoi n'est pas renvoyée, sauf une fois par jour :
    /// ce qui aurait été effacé à la main dans le calendrier « Kairn » revient alors.
    /// </summary>
    private static async Task PushAsync(CalendarAccount a, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-DaysBefore);
        var to = today.AddDays(DaysAfter);

        var wanted = Storage.Data.Tasks
            .Where(t => !t.IsExternal && !t.Floating && !t.IsBreak && t.Date >= from && t.Date <= to)
            .ToDictionary(CalDav.OwnUid);
        bool full = DateTime.Now - a.LastFullPush > TimeSpan.FromDays(1);
        var pushed = a.Pushed.ToHashSet();
        CalDavLog.Line($"push : {wanted.Count} tâche(s) dans la fenêtre, {pushed.Count} déjà là-bas{(full ? ", envoi complet du jour" : "")}");

        int sent = 0, removed = 0;
        bool dirty = false;
        try
        {
            foreach (var (uid, task) in wanted)
            {
                ct.ThrowIfCancellationRequested();
                var hash = IcsService.EventHash(task, uid);
                if (!full && pushed.Contains(uid) && a.PushedHash.GetValueOrDefault(uid) == hash) continue;
                await CalDav.PutAsync(a, a.WriteHref!, task, ct);
                // Noté aussitôt : même si la suite échoue, Kairn sait ce qu'il a déposé là-bas (et pourra le retirer).
                if (pushed.Add(uid)) a.Pushed.Add(uid);
                a.PushedHash[uid] = hash;
                dirty = true;
                sent++;
            }

            // Supprimé ou déplacé hors de la fenêtre depuis la dernière fois : on retire ce qu'on avait posé.
            foreach (var gone in pushed.Where(uid => !wanted.ContainsKey(uid)).ToList())
            {
                ct.ThrowIfCancellationRequested();
                await CalDav.DeleteAsync(a, a.WriteHref!, gone, ct);
                a.Pushed.Remove(gone);
                a.PushedHash.Remove(gone);
                dirty = true;
                removed++;
            }

            if (full)
            {
                a.LastFullPush = DateTime.Now;
                dirty = true;
            }
            CalDavLog.Line($"push : {sent} envoyée(s), {removed} retirée(s)");
        }
        finally
        {
            if (dirty) Storage.SaveSettings();
        }
    }

    /// <summary>Retire tout ce que Kairn a déposé sur iCloud (quand on décoche l'envoi).</summary>
    public static async Task ClearPushedAsync()
    {
        if (Account is not { WriteHref.Length: > 0 } a || a.Pushed.Count == 0) return;
        try
        {
            foreach (var uid in a.Pushed.ToList())
                await CalDav.DeleteAsync(a, a.WriteHref!, uid, CancellationToken.None);
        }
        catch { /* ce qui reste là-bas peut être supprimé à la main : on ne bloque pas le réglage */ }
        a.Pushed = [];
        a.PushedHash = [];
        Storage.SaveSettings();
    }
}
