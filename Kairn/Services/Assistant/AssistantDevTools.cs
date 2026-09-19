using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kairn.Models;

namespace Kairn.Services.Assistant;

/// <summary>
/// Commandes de test (développement) : installer le modèle local sans interface,
/// ou générer un programme complet à partir d'un fichier et écrire le résultat, sans rien ajouter au calendrier.
/// </summary>
public static class AssistantDevTools
{
    public static async void Run(string[] args)
    {
        var log = Path.Combine(Storage.Root, "assistant-dev.log");
        void Log(string s) { try { File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss}] {s}\n"); } catch { } }
        try
        {
            switch (args[0])
            {
                case "--assistant-install":
                    string lastStep = ""; int lastPct = -1;
                    await LocalEngine.InstallAsync(new Progress<(string Step, double Fraction)>(p =>
                    {
                        int pct = (int)(p.Fraction * 100);
                        if (p.Step != lastStep || pct / 5 != lastPct / 5) { lastStep = p.Step; lastPct = pct; Log($"{p.Step} {pct}%"); }
                    }), CancellationToken.None);
                    Log("installed: " + LocalEngine.IsInstalled);
                    break;

                case "--assistant-demo":
                    var req = JsonNode.Parse(File.ReadAllText(args[1]))!;
                    bool online = req["online"]?.GetValue<bool>() ?? false;
                    var g = new Goal
                    {
                        Request = req["request"]!.GetValue<string>(),
                        Start = DateOnly.FromDateTime(DateTime.Now),
                        Deadline = DateOnly.FromDateTime(DateTime.Now).AddDays(req["days"]?.GetValue<int>() ?? 56),
                        Days = (req["weekdays"]?.AsArray().Select(d => (DayOfWeek)d!.GetValue<int>()).ToList()) ?? [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
                        Window = Enum.Parse<GoalWindow>(req["window"]?.GetValue<string>() ?? "Evening"),
                        SessionMinutes = req["minutes"]?.GetValue<int>() ?? 45,
                    };
                    var llm = GoalAssistant.Create(online);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var frame = await GoalAssistant.FrameAsync(llm, g, CancellationToken.None);
                    Log($"frame {sw.Elapsed.TotalSeconds:0.0}s");
                    g.Title = frame.Title;
                    var answers = req["answers"]?.AsArray().Select(a => a!.GetValue<string>()).ToList() ?? [];
                    g.Answers = frame.Questions.Select((q, i) => new GoalQuestion { Question = q, Answer = i < answers.Count ? answers[i] : "" }).ToList();
                    sw.Restart();
                    var plan = await GoalAssistant.PlanAsync(llm, g, CancellationToken.None);
                    Log($"plan {sw.Elapsed.TotalSeconds:0.0}s");
                    g.Phases = plan.Phases;
                    g.Summary = plan.Summary;
                    // Pour les captures de l'aperçu (--snapshot).
                    File.WriteAllText(Path.Combine(Storage.Root, "assistant-dev-goal.json"), JsonSerializer.Serialize(new { Goal = g, plan.Sessions }));
                    var (placed, unplaced) = GoalScheduler.Place(g, plan.Sessions, g.Start);
                    var result = new
                    {
                        frame,
                        plan.Title, plan.Summary, plan.Phases,
                        Sessions = plan.Sessions,
                        Placed = placed.Select(t => new { t.Date, Time = t.TimeRange, t.Title }),
                        Unplaced = unplaced.Count,
                    };
                    File.WriteAllText(args[2], JsonSerializer.Serialize(result, new JsonSerializerOptions
                    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                    break;
            }
        }
        catch (Exception ex) { Log("ERROR " + ex); }
        finally
        {
            LocalEngine.Stop();
            System.Windows.Application.Current.Shutdown();
        }
    }
}
