using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Kairn.Models;

namespace Kairn.Services.Assistant;

/// <summary>Premier échange : un titre court, 0 à 3 questions pour préciser, et un avis sur le réalisme.</summary>
public record GoalFrame(string Title, List<string> Questions, string Feasibility, string Note);

/// <summary>Le programme proposé : feuille de route complète + séances détaillées des deux prochaines semaines.</summary>
public record GoalPlan(string Title, string Summary, List<GoalPhase> Phases, List<GoalSession> Sessions);

/// <summary>
/// L'assistant d'objectifs. Le modèle découpe (phases, séances, consignes, ressources) ;
/// le calage dans le calendrier est fait ensuite par <see cref="GoalScheduler"/>, sans IA, pour des dates toujours justes.
/// </summary>
public static class GoalAssistant
{
    private static AssistantSettings S => Storage.Settings.Assistant;

    // ===================== Choix du moteur =====================

    /// <summary>Le mode en ligne est configuré (clé enregistrée et mode activé).</summary>
    public static bool OnlineReady => S.OnlineEnabled && Secret.Unprotect(S.ProtectedKey ?? "") is { Length: > 0 };

    public static bool LocalReady => S.LocalSource switch
    {
        "ollama" or "lmstudio" => true, // vérifié au moment de l'appel
        _ => LocalEngine.IsInstalled,
    };

    public static ILlm Create(bool online)
    {
        if (online)
        {
            var key = Secret.Unprotect(S.ProtectedKey ?? "") ?? throw new LlmException("no key");
            return S.OnlineProvider == "openai"
                ? new OpenAiCompatLlm(string.IsNullOrWhiteSpace(S.OnlineBaseUrl) ? "https://api.openai.com/v1" : S.OnlineBaseUrl!,
                                      string.IsNullOrWhiteSpace(S.OnlineModel) ? "gpt-4o-mini" : S.OnlineModel!, key, online: true)
                : new ClaudeLlm(key, S.OnlineModel);
        }
        return S.LocalSource switch
        {
            "ollama" => new OpenAiCompatLlm("http://127.0.0.1:11434/v1", S.LocalModel ?? "qwen3:4b", null, false),
            "lmstudio" => new OpenAiCompatLlm("http://127.0.0.1:1234/v1", S.LocalModel ?? "local-model", null, false),
            _ => new LazyLocalLlm(),
        };
    }

    /// <summary>Ollama ou LM Studio répondent-ils sur ce PC ? (appel local uniquement)</summary>
    public static async Task<List<string>> DetectLocalServerAsync(string source)
    {
        var url = source == "ollama" ? "http://127.0.0.1:11434/v1/models" : "http://127.0.0.1:1234/v1/models";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var json = JsonNode.Parse(await http.GetStringAsync(url));
            return json?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? [];
        }
        catch { return []; }
    }

    /// <summary>Le moteur intégré démarre au premier appel, et s'arrête tout seul ensuite.</summary>
    private sealed class LazyLocalLlm : ILlm
    {
        public bool IsOnline => false;
        public bool CanSearch => false;
        public async Task<JsonNode> JsonAsync(string system, string user, JsonObject schema, bool webSearch, CancellationToken ct)
        {
            var url = await LocalEngine.EnsureRunningAsync(S.UseGpu, ct);
            return await new OpenAiCompatLlm(url, "local", null, false, LocalEngine.Release).JsonAsync(system, user, schema, false, ct);
        }
    }

    // ===================== Consignes =====================

    private static string Language => Loc.Instance.Current.NativeName;

    /// <summary>Consignes communes. <paramref name="strict"/> : petit modèle local, qui invente volontiers raccourcis et menus.</summary>
    private static string BaseSystem(bool strict) => $"""
        You are Kairn's planning assistant. Kairn helps people with attention difficulties (ADHD) reach goals through small, regular work sessions.
        Rules:
        - Write every text for the user in {Language}. Use a warm, simple, non-judgmental tone. Address the user informally.
        - Be concrete: each session has one clear, doable action (e.g. "20 min: 50 quick sketches of cylinders from different angles"), never vague advice.
        - The very first session must be very easy, to make starting effortless. Difficulty then increases gradually.
        - Estimates must be realistic; never plan more than the time given.
        - Do not invent facts you are unsure of (exact menu names, prices, dates). Stay generic rather than wrong.
        - Before planning, recall how good teachers, courses and books teach this skill, and follow that proven progression
          (fundamentals first, then combining them, then real projects). Practice must build real skill, not trivial busywork.
        - Take into account everything the user said (level, style, material, precise target).
        - {(strict ? "Never mention keyboard shortcuts, key names or menu names" : "For software, do not give exact menu paths or keyboard shortcuts unless you are certain of them")}: describe the action
          (e.g. "add a cube and make it taller") and let the linked tutorial show how.
        - Always address the user the same informal way.
        - Session titles: short and specific (max 6 words), with no numbering and without the word "session".
        """;

    private static JsonObject Obj(JsonObject props, params string[] required)
    {
        var req = new JsonArray();
        foreach (var r in required) req.Add(r);
        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req, ["additionalProperties"] = false };
    }
    private static JsonObject Str() => new() { ["type"] = "string" };
    private static JsonObject Int() => new() { ["type"] = "integer" };
    private static JsonObject Num() => new() { ["type"] = "number" };
    private static JsonObject Arr(JsonObject item) => new() { ["type"] = "array", ["items"] = item };

    private static string Context(Goal g)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Goal (user's words): {g.Request}");
        sb.AppendLine($"Today: {DateTime.Today:yyyy-MM-dd}. Deadline: {g.Deadline:yyyy-MM-dd} ({Weeks(g):0.#} weeks).");
        sb.AppendLine($"Availability: {g.SessionsPerWeek} sessions per week, {g.SessionMinutes} minutes each (total ≈ {TotalHours(g):0.#} hours).");
        foreach (var qa in g.Answers.Where(a => !string.IsNullOrWhiteSpace(a.Answer)))
            sb.AppendLine($"Q: {qa.Question}\nA: {qa.Answer}");
        return sb.ToString();
    }

    public static double Weeks(Goal g) => Math.Max(1, (g.Deadline.DayNumber - g.Start.DayNumber + 1) / 7.0);
    public static double TotalHours(Goal g) => Weeks(g) * g.SessionsPerWeek * g.SessionMinutes / 60.0;
    private static int MinMinutes(Goal g) => Math.Max(10, g.SessionMinutes - 15);

    /// <summary>« Session 3 : Proportions » → « Proportions ».</summary>
    private static string CleanTitle(string t) =>
        System.Text.RegularExpressions.Regex.Replace(t.Trim(), @"^(session|séance|sesión|sessão|sitzung|day|jour|étape|step)?\s*\d+\s*[:.\-–)]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    // ===================== 1. Cadrage =====================

    public static async Task<GoalFrame> FrameAsync(ILlm llm, Goal g, CancellationToken ct)
    {
        var schema = Obj(new JsonObject
        {
            ["title"] = Str(),
            ["questions"] = Arr(Str()),
            ["feasibility"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("ok", "tight", "unrealistic") },
            ["note"] = Str(),
        }, "title", "questions", "feasibility", "note");

        var user = Context(g) + """

            Task: 1) give the goal a short title (max 5 words). 2) Ask 0 to 3 short questions ONLY if the answers would really change the plan
            (current level, precise target, material available...). No questions about schedule: it is already known.
            3) Judge feasibility of the goal in the time available ("ok", "tight" or "unrealistic") and write a one-sentence note
            (if tight or unrealistic, suggest a lighter version of the goal).
            """;
        var j = await llm.JsonAsync(BaseSystem(!llm.IsOnline), user, schema, false, ct);
        return new GoalFrame(
            j["title"]?.GetValue<string>() ?? g.Request,
            j["questions"]?.AsArray().Select(q => q?.GetValue<string>() ?? "").Where(q => q.Length > 0).Take(3).ToList() ?? [],
            j["feasibility"]?.GetValue<string>() ?? "ok",
            j["note"]?.GetValue<string>() ?? "");
    }

    // ===================== 2. Programme =====================

    private static JsonObject SessionsSchema(bool withResources) => Arr(Obj(new JsonObject
    {
        ["title"] = Str(),
        ["minutes"] = Int(),
        ["instructions"] = Str(),
        ["phase"] = Int(),
        [withResources ? "resources" : "search"] = withResources
            ? Arr(Obj(new JsonObject { ["title"] = Str(), ["url"] = Str() }, "title", "url"))
            : Str(),
    }, "title", "minutes", "instructions", "phase", withResources ? "resources" : "search"));

    /// <summary>Nombre de séances à détailler : les deux prochaines semaines (ou moins si l'échéance est proche).</summary>
    public static int WaveSize(Goal g, DateOnly from) =>
        Math.Max(1, (int)Math.Ceiling(Math.Min(14, g.Deadline.DayNumber - from.DayNumber + 1) / 7.0 * g.SessionsPerWeek));

    public static async Task<GoalPlan> PlanAsync(ILlm llm, Goal g, CancellationToken ct)
    {
        bool search = llm.CanSearch && S.WebSearch;
        int count = WaveSize(g, g.Start);
        var schema = Obj(new JsonObject
        {
            ["title"] = Str(),
            ["summary"] = Str(),
            ["phases"] = Arr(Obj(new JsonObject { ["title"] = Str(), ["summary"] = Str(), ["weeks"] = Num() }, "title", "summary", "weeks")),
            ["sessions"] = SessionsSchema(search),
        }, "title", "summary", "phases", "sessions");

        var user = Context(g) + $"""

            Task:
            1) Roadmap: split the whole period ({Weeks(g):0.#} weeks) into 3 to 6 phases (title, one-sentence summary, number of weeks; the weeks must add up to the period).
            2) Detail exactly {count} sessions for the first two weeks (phase = index of the phase, starting at 0).
               Each session: a short title, minutes (between {MinMinutes(g)} and {g.SessionMinutes}), and instructions: 2 to 4 short numbered steps explaining exactly what to do and why.
            {(search
                ? "3) For each session, use web search to find 1 or 2 real, free, high-quality resources (videos, tutorials, articles) that match it. Only give URLs that you actually found in search results."
                : "3) For each session, give a short web search query (in the user's language) to find a tutorial for it.")}
            4) summary: two sentences presenting the program.
            """;
        var j = await llm.JsonAsync(BaseSystem(!llm.IsOnline), user, schema, search, ct);

        var phases = j["phases"]?.AsArray().Select(p => new GoalPhase
        {
            Title = p?["title"]?.GetValue<string>() ?? "",
            Summary = p?["summary"]?.GetValue<string>() ?? "",
            Weeks = p?["weeks"]?.GetValue<double>() ?? 1,
        }).Where(p => p.Title.Length > 0).ToList() ?? [];
        // Les phases doivent couvrir toute la période : on remet les durées à l'échelle si le modèle a mal compté.
        double sum = phases.Sum(p => Math.Max(0.5, p.Weeks));
        if (sum > 0) foreach (var p in phases) p.Weeks = Math.Round(Math.Max(0.5, p.Weeks) * Weeks(g) / sum * 2) / 2;
        return new GoalPlan(j["title"]?.GetValue<string>() ?? g.Title, j["summary"]?.GetValue<string>() ?? "", phases,
                            ReadSessions(j["sessions"], g, search).Take(count).ToList());
    }

    /// <summary>La vague suivante, en tenant compte de ce qui a été fait, sauté, et des retours.</summary>
    public static async Task<List<GoalSession>> NextAsync(ILlm llm, Goal g, DateOnly from, CancellationToken ct)
    {
        bool search = llm.CanSearch && S.WebSearch;
        int count = WaveSize(g, from);
        var tasks = Storage.Data.Tasks.Where(t => t.GoalId == g.Id).OrderBy(t => t.Date).ToList();
        var done = tasks.Where(t => t.Done).Select(t => t.Title).ToList();
        var skipped = tasks.Where(t => !t.Done && (t.Floating || t.Date < DateOnly.FromDateTime(DateTime.Now))).Select(t => t.Title).ToList();
        int weekIndex = (from.DayNumber - g.Start.DayNumber) / 7;

        var sb = new StringBuilder(Context(g));
        sb.AppendLine("Roadmap:");
        for (int i = 0; i < g.Phases.Count; i++) sb.AppendLine($"{i}. {g.Phases[i].Title} ({g.Phases[i].Weeks:0.#} weeks): {g.Phases[i].Summary}");
        sb.AppendLine($"We are now at week {weekIndex + 1}.");
        sb.AppendLine("Sessions done: " + (done.Count == 0 ? "none" : string.Join("; ", done)));
        if (skipped.Count > 0) sb.AppendLine("Sessions not done: " + string.Join("; ", skipped));
        if (g.Feedback.Count > 0) sb.AppendLine("User feedback: " + string.Join("; ", g.Feedback.TakeLast(3)));
        sb.AppendLine($"""

            Task: detail exactly {count} next sessions (the next two weeks), following the roadmap from where the user really is.
            If sessions were not done, include what matters from them first. Adapt difficulty to the feedback.
            Each session: title, minutes (between {MinMinutes(g)} and {g.SessionMinutes}), phase index, instructions (2 to 4 numbered steps).
            {(search ? "For each session, use web search to find 1 or 2 real resources; only give URLs found in search results." : "For each session, give a short web search query.")}
            """);
        var schema = Obj(new JsonObject { ["sessions"] = SessionsSchema(search) }, "sessions");
        var j = await llm.JsonAsync(BaseSystem(!llm.IsOnline), sb.ToString(), schema, search, ct);
        return ReadSessions(j["sessions"], g, search).Take(count).ToList();
    }

    private static IEnumerable<GoalSession> ReadSessions(JsonNode? arr, Goal g, bool withResources)
    {
        foreach (var s in arr?.AsArray() ?? [])
        {
            if (s is null) continue;
            var session = new GoalSession
            {
                Title = CleanTitle(s["title"]?.GetValue<string>() ?? ""),
                Minutes = Math.Clamp(s["minutes"]?.GetValue<int>() ?? g.SessionMinutes, 10, g.SessionMinutes),
                Instructions = s["instructions"]?.GetValue<string>()?.Trim() ?? "",
                Phase = Math.Max(0, s["phase"]?.GetValue<int>() ?? 0),
            };
            if (session.Title.Length == 0) continue;
            if (withResources)
            {
                foreach (var r in s["resources"]?.AsArray() ?? [])
                {
                    var url = r?["url"]?.GetValue<string>()?.Trim() ?? "";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) continue;
                    session.Resources.Add(new TaskLink { Title = r?["title"]?.GetValue<string>() ?? Launcher.NameFor(url), Target = url });
                }
            }
            else if (s["search"]?.GetValue<string>() is { Length: > 0 } query)
            {
                // En local : pas d'adresse inventée, un lien de recherche. Rien n'est envoyé tant que la personne ne clique pas.
                session.Resources.Add(new TaskLink
                {
                    Title = L.F("goal.searchLink", query),
                    Target = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query),
                });
            }
            yield return session;
        }
    }
}
