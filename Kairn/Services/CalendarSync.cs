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
/// toutes les dix minutes, au retour sur la fenêtre, et quand on clique sur « Rafraîchir ».
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

    private static CalendarAccount? Account => Storage.Settings.Calendar;
    public static bool Linked => Account is { User.Length: > 0, HomeUrl.Length: > 0 };

    public static void Start()
    {
        Timer.Tick += (_, _) => _ = SyncAsync();
        Timer.Start();
        if (Linked) _ = SyncAsync();
    }

    public static void Stop() => Timer.Stop();

    /// <summary>Coupe le lien : les rendez-vous importés disparaissent de Kairn, rien n'est touché côté Apple.</summary>
    public static void Unlink()
    {
        Storage.Settings.Calendar = null;
        Storage.SaveSettings();
        Storage.Data.Tasks.RemoveAll(t => t.IsExternal);
        Storage.Save();
        Status = "";
        Changed?.Invoke();
    }

    public static async Task SyncAsync()
    {
        if (Busy || Account is not { } a || !Linked) return;
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

        a.LastSync = DateTime.Now;
        Storage.SaveSettings();
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

        var names = (await CalDav.ListAsync(a, ct)).ToDictionary(c => c.Href, c => c.Name);
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

        Storage.Data.Tasks.RemoveAll(t => t.IsExternal && t.Date >= from && t.Date <= to);
        // Un rendez-vous qui tomberait exactement sur une tâche de Kairn ne l'écrase pas : les deux coexistent.
        Storage.Data.Tasks.AddRange(fetched);
        Storage.Save();
    }

    // ===================== Kairn → iCloud =====================

    /// <summary>
    /// Fait remonter les tâches planifiées, et seulement dans le calendrier « Kairn ».
    /// Ce qui a disparu côté Kairn est retiré là-bas ; rien d'autre n'est jamais touché.
    /// </summary>
    private static async Task PushAsync(CalendarAccount a, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-DaysBefore);
        var to = today.AddDays(DaysAfter);

        var mine = Storage.Data.Tasks
            .Where(t => !t.IsExternal && !t.Floating && !t.IsBreak && t.Date >= from && t.Date <= to)
            .ToList();

        var wanted = mine.ToDictionary(CalDav.OwnUid);
        CalDavLog.Line($"push : {wanted.Count} tâche(s) à déposer, {a.Pushed.Count} déjà là-bas");
        foreach (var (uid, task) in wanted)
        {
            ct.ThrowIfCancellationRequested();
            await CalDav.PutAsync(a, a.WriteHref!, task, ct);
        }

        // Supprimé ou déplacé hors de la fenêtre depuis la dernière fois : on retire ce qu'on avait posé.
        foreach (var gone in a.Pushed.Where(uid => !wanted.ContainsKey(uid)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            await CalDav.DeleteAsync(a, a.WriteHref!, gone, ct);
        }

        a.Pushed = [.. wanted.Keys];
        Storage.SaveSettings();
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
        Storage.SaveSettings();
    }
}
