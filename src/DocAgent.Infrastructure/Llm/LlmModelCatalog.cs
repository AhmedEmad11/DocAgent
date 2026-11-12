using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocAgent.Core.Entities;

namespace DocAgent.Infrastructure.Llm;

public sealed record LlmModelInfo(string Id, string Name);

/// <summary>
/// Lists a provider's models that can call tools (SRS KEY-5). OpenRouter reports tool support per model
/// (<c>supported_parameters</c> contains "tools"); the other three APIs do not, so they use documented rules:
/// Anthropic: every <c>claude-*</c> model; Gemini: models offering <c>generateContent</c> whose id starts with "gemini-"
/// and is not an embedding / speech / image / live variant; OpenAI: ids starting <c>gpt-</c>, <c>chatgpt-</c>, <c>o1</c>, <c>o3</c>,
/// <c>o4</c> or <c>o5</c> minus audio, realtime, transcription, speech, image, search, instruct, codex and similar variants.
/// </summary>
public sealed partial class LlmModelCatalog(LlmHttpClients http)
{
    private const int MaxModels = 500;

    public async Task<IReadOnlyList<LlmModelInfo>> ListToolModelsAsync(LlmProvider provider, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LlmProviders.ModelsEndpoint(provider));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        switch (provider)
        {
            case LlmProvider.Anthropic:
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                break;
            case LlmProvider.Gemini:
                request.Headers.Add("x-goog-api-key", apiKey);
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                break;
        }

        try
        {
            // Non-success statuses surface as LlmProviderException from ProviderHttpHandler.
            using var response = await http.For(provider).SendAsync(request, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Parse(provider, document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException && !ct.IsCancellationRequested)
        {
            throw new LlmProviderException(ex is JsonException ? LlmFailureKind.Rejected : LlmFailureKind.Unavailable);
        }
    }

    public static IReadOnlyList<LlmModelInfo> Parse(LlmProvider provider, JsonElement root)
    {
        var models = new List<LlmModelInfo>();
        var list = root.TryGetProperty(provider == LlmProvider.Gemini ? "models" : "data", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : default;
        foreach (var item in list)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var info = provider switch
            {
                LlmProvider.OpenRouter => OpenRouter(item),
                LlmProvider.Anthropic => Anthropic(item),
                LlmProvider.OpenAI => OpenAI(item),
                _ => Gemini(item),
            };
            if (info is not null)
            {
                models.Add(info);
            }
        }

        return [.. models.DistinctBy(m => m.Id).OrderBy(m => m.Id, StringComparer.Ordinal).Take(MaxModels)];
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static LlmModelInfo? OpenRouter(JsonElement e)
    {
        var id = Str(e, "id");
        if (id is null || !e.TryGetProperty("supported_parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array
            || !parameters.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "tools"))
        {
            return null;
        }

        return new LlmModelInfo(id, Str(e, "name") ?? id);
    }

    private static LlmModelInfo? Anthropic(JsonElement e) =>
        Str(e, "id") is { } id && id.StartsWith("claude-", StringComparison.Ordinal) ? new LlmModelInfo(id, Str(e, "display_name") ?? id) : null;

    private static LlmModelInfo? OpenAI(JsonElement e) =>
        Str(e, "id") is { } id && OpenAIChatModel().IsMatch(id) && !OpenAIExcluded().IsMatch(id) ? new LlmModelInfo(id, id) : null;

    private static LlmModelInfo? Gemini(JsonElement e)
    {
        var name = Str(e, "name");
        if (name is null || !name.StartsWith("models/gemini-", StringComparison.Ordinal) || GeminiExcluded().IsMatch(name)
            || !e.TryGetProperty("supportedGenerationMethods", out var methods) || methods.ValueKind != JsonValueKind.Array
            || !methods.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == "generateContent"))
        {
            return null;
        }

        var id = name["models/".Length..];
        return new LlmModelInfo(id, Str(e, "displayName") ?? id);
    }

    [GeneratedRegex(@"^(gpt-|chatgpt-|o[1345](-|$))")]
    private static partial Regex OpenAIChatModel();

    [GeneratedRegex("audio|realtime|transcribe|tts|image|search|instruct|embedding|moderation|codex|computer-use|deep-research|davinci|babbage", RegexOptions.IgnoreCase)]
    private static partial Regex OpenAIExcluded();

    [GeneratedRegex("embedding|tts|image|live|audio|aqa|learnlm|robotics|computer-use|native-dialog|imagen|veo", RegexOptions.IgnoreCase)]
    private static partial Regex GeminiExcluded();
}
