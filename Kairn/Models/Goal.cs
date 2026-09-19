using System.Text.Json.Serialization;

namespace Kairn.Models;

/// <summary>Moment de la journée où l'assistant place les séances.</summary>
public enum GoalWindow { Morning, Afternoon, Evening, Any }

/// <summary>
/// Un objectif suivi par l'assistant : « savoir dessiner des personnages en 2 mois, le soir en semaine ».
/// La feuille de route (phases) est faite une fois ; les séances sont détaillées par vagues de deux semaines.
/// </summary>
public class Goal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Titre court (« Dessin de personnages »).</summary>
    public string Title { get; set; } = "";
    /// <summary>Ce que la personne a écrit.</summary>
    public string Request { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateOnly Start { get; set; }
    public DateOnly Deadline { get; set; }
    public List<DayOfWeek> Days { get; set; } = [];
    public GoalWindow Window { get; set; } = GoalWindow.Evening;
    public int SessionMinutes { get; set; } = 45;
    public List<GoalQuestion> Answers { get; set; } = [];
    public List<GoalPhase> Phases { get; set; } = [];
    /// <summary>Catégorie de la bibliothèque où sont rangées les ressources.</summary>
    public string? CategoryId { get; set; }
    /// <summary>Plan fait avec l'IA en ligne (sinon : en local).</summary>
    public bool Online { get; set; }
    /// <summary>Dernier jour déjà détaillé en séances.</summary>
    public DateOnly PlannedUntil { get; set; }
    /// <summary>Retours donnés à l'assistant (« trop facile »…), utilisés pour la vague suivante.</summary>
    public List<string> Feedback { get; set; } = [];
    public DateTime Created { get; set; } = DateTime.Now;

    [JsonIgnore] public int SessionsPerWeek => Math.Max(1, Days.Count);
}

public class GoalQuestion
{
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
}

public class GoalPhase
{
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public double Weeks { get; set; }
}

/// <summary>Une séance proposée par l'assistant, avant d'être placée dans le calendrier.</summary>
public class GoalSession
{
    public string Title { get; set; } = "";
    public int Minutes { get; set; }
    public string Instructions { get; set; } = "";
    public int Phase { get; set; }
    public List<TaskLink> Resources { get; set; } = [];
}

/// <summary>Réglages de l'assistant (modèle local, IA en ligne).</summary>
public class AssistantSettings
{
    /// <summary>« builtin » (modèle intégré), « ollama » ou « lmstudio ».</summary>
    public string LocalSource { get; set; } = "builtin";
    public bool UseGpu { get; set; } = true;
    /// <summary>Modèle choisi dans Ollama / LM Studio.</summary>
    public string? LocalModel { get; set; }
    /// <summary>Mode en ligne activé (sinon rien ne sort du PC).</summary>
    public bool OnlineEnabled { get; set; }
    /// <summary>« claude » ou « openai » (compatible OpenAI).</summary>
    public string OnlineProvider { get; set; } = "claude";
    /// <summary>Clé d'API chiffrée par Windows (DPAPI).</summary>
    public string? ProtectedKey { get; set; }
    public string? OnlineModel { get; set; }
    public string? OnlineBaseUrl { get; set; }
    /// <summary>Recherche web pour trouver de vraies ressources (Claude uniquement).</summary>
    public bool WebSearch { get; set; } = true;
}
