using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Kairn.Services.Assistant;

/// <summary>
/// Mode en ligne avec Claude (clé d'API de la personne). Avec la recherche web, Claude trouve de vraies ressources ;
/// sans elle, la réponse est contrainte par le schéma JSON.
/// </summary>
public sealed class ClaudeLlm(string apiKey, string? model) : ILlm
{
    public const string DefaultModel = "claude-opus-5";

    private readonly AnthropicClient _client = new() { ApiKey = apiKey };
    private string Model => string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

    public bool IsOnline => true;
    public bool CanSearch => true;

    public async Task<JsonNode> JsonAsync(string system, string user, JsonObject schema, bool webSearch, CancellationToken ct)
    {
        try
        {
            if (!webSearch) return JsonText.Parse(await AskAsync(system, user, schema, search: false, ct));

            // Recherche web et format JSON imposé ne se combinent pas (les résultats portent des citations) :
            // on demande le JSON dans le texte, et si besoin un second passage sans outil le remet au propre.
            var text = await AskAsync(system + "\n\nAnswer with a single JSON object that matches this JSON schema, and nothing else:\n" + schema.ToJsonString(),
                                      user, null, search: true, ct);
            try { return JsonText.Parse(text); }
            catch (LlmException)
            {
                return JsonText.Parse(await AskAsync("Convert the content below into a JSON object matching the schema. Keep every URL exactly as written.",
                                                     text, schema, search: false, ct));
            }
        }
        catch (Anthropic.Exceptions.AnthropicApiException ex)
        {
            throw new LlmException(ex.Message);
        }
    }

    private async Task<string> AskAsync(string system, string user, JsonObject? schema, bool search, CancellationToken ct)
    {
        var p = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 16000,
            System = system,
            Messages = [new() { Role = Role.User, Content = user }],
            // Un refus éventuel est repris par un autre modèle dans le même appel.
            Betas = [Anthropic.Models.Beta.AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
        };
        if (search) p = p with { Tools = [new BetaToolUnion(new BetaWebSearchTool20260209 { MaxUses = 8 })] };
        if (schema != null)
            p = p with
            {
                OutputConfig = new BetaOutputConfig
                {
                    Format = new BetaJsonOutputFormat
                    {
                        Schema = schema.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
                    },
                },
            };

        var msg = await _client.Beta.Messages.Create(p, ct);
        if (msg.StopReason == "refusal") throw new LlmException("refusal");
        return string.Concat(msg.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
    }
}
