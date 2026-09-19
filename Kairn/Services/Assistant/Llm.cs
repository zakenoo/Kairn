using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kairn.Services.Assistant;

/// <summary>Un modèle de langage qui répond en JSON conforme à un schéma.</summary>
public interface ILlm
{
    /// <summary>Envoie-t-il la demande hors du PC ?</summary>
    bool IsOnline { get; }
    /// <summary>Sait-il chercher de vraies ressources sur Internet ?</summary>
    bool CanSearch { get; }
    Task<JsonNode> JsonAsync(string system, string user, JsonObject schema, bool webSearch, CancellationToken ct);
}

/// <summary>
/// Protocole « compatible OpenAI » : le moteur intégré (llama-server), Ollama, LM Studio,
/// ou un service en ligne compatible. La sortie est contrainte par le schéma JSON.
/// </summary>
public sealed class OpenAiCompatLlm(string baseUrl, string model, string? apiKey, bool online, Action? afterEach = null) : ILlm
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public bool IsOnline => online;
    public bool CanSearch => false;

    public async Task<JsonNode> JsonAsync(string system, string user, JsonObject schema, bool webSearch, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.5,
            ["max_tokens"] = 6000,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "answer", ["strict"] = true, ["schema"] = schema.DeepClone() },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new("Bearer", apiKey);
        try
        {
            using var resp = await Http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new LlmException($"HTTP {(int)resp.StatusCode}: {Trim(text)}");
            var content = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            return JsonText.Parse(content);
        }
        finally { afterEach?.Invoke(); }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}

/// <summary>Erreur lisible remontée à l'interface.</summary>
public sealed class LlmException(string message) : Exception(message);

public static class JsonText
{
    /// <summary>Lit le JSON d'une réponse, même entouré de texte ou d'un bloc ```json.</summary>
    public static JsonNode Parse(string content)
    {
        var s = content.Trim();
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        if (a < 0 || b <= a) throw new LlmException("no JSON in answer");
        try { return JsonNode.Parse(s[a..(b + 1)]) ?? throw new LlmException("empty JSON"); }
        catch (JsonException ex) { throw new LlmException("invalid JSON: " + ex.Message); }
    }
}
