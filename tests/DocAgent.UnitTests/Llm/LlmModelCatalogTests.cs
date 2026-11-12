using DocAgent.TestSupport;
using System.Text.Json;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;

namespace DocAgent.UnitTests.Llm;

/// <summary>Model listing filtered to tool-capable models (SRS KEY-5).</summary>
public sealed class LlmModelCatalogTests
{
    private static async Task<(IReadOnlyList<LlmModelInfo> Models, StubTransport.RecordedRequest Request)> ListAsync(LlmProvider provider, string json)
    {
        var clients = LlmTest.Clients(_ => StubTransport.Json(json), out var transport);
        var models = await new LlmModelCatalog(clients).ListToolModelsAsync(provider, LlmTest.SecretKey, default);
        return (models, Assert.Single(transport.Requests));
    }

    [Fact]
    public async Task OpenRouter_keeps_models_that_report_the_tools_parameter()
    {
        var (models, request) = await ListAsync(LlmProvider.OpenRouter, """
            {"data":[
              {"id":"openai/gpt-4o-mini","name":"GPT-4o mini","supported_parameters":["temperature","tools","tool_choice"]},
              {"id":"some/no-tools","name":"No tools","supported_parameters":["temperature"]},
              {"id":"meta/free:free","name":"Free","supported_parameters":["tools"]},
              {"id":"broken/no-params"}]}
            """);

        Assert.Equal(["meta/free:free", "openai/gpt-4o-mini"], models.Select(m => m.Id));
        Assert.Equal("GPT-4o mini", models.Single(m => m.Id == "openai/gpt-4o-mini").Name);
        Assert.Equal("Bearer " + LlmTest.SecretKey, request.Headers["authorization"]);
        Assert.Equal("https://openrouter.ai/api/v1/models", request.Url.ToString());
    }

    [Fact]
    public async Task Anthropic_keeps_claude_models_and_sends_the_version_header()
    {
        var (models, request) = await ListAsync(LlmProvider.Anthropic, """
            {"data":[{"type":"model","id":"claude-sonnet-4-5","display_name":"Claude Sonnet 4.5"},{"type":"model","id":"other-model","display_name":"x"}],"has_more":false}
            """);

        Assert.Equal("claude-sonnet-4-5", Assert.Single(models).Id);
        Assert.Equal(LlmTest.SecretKey, request.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"]);
        Assert.StartsWith("https://api.anthropic.com/v1/models", request.Url.ToString());
    }

    [Fact]
    public async Task OpenAI_uses_an_allowlist_of_chat_model_families()
    {
        var (models, request) = await ListAsync(LlmProvider.OpenAI, """
            {"data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"},{"id":"o3-mini"},{"id":"o1"},{"id":"gpt-4o-audio-preview"},{"id":"gpt-4o-realtime-preview"},
                     {"id":"text-embedding-3-small"},{"id":"whisper-1"},{"id":"dall-e-3"},{"id":"tts-1"},{"id":"gpt-image-1"},{"id":"omni-moderation-latest"},
                     {"id":"gpt-3.5-turbo-instruct"},{"id":"gpt-4o-transcribe"},{"id":"gpt-4o-search-preview"},{"id":"davinci-002"}]}
            """);

        Assert.Equal(["gpt-4o", "gpt-4o-mini", "o1", "o3-mini"], models.Select(m => m.Id));
        Assert.Equal("Bearer " + LlmTest.SecretKey, request.Headers["authorization"]);
    }

    [Fact]
    public async Task Gemini_needs_generateContent_and_skips_embedding_speech_image_and_live_models()
    {
        var (models, request) = await ListAsync(LlmProvider.Gemini, """
            {"models":[
              {"name":"models/gemini-2.5-flash","displayName":"Gemini 2.5 Flash","supportedGenerationMethods":["generateContent","countTokens"]},
              {"name":"models/gemini-2.5-pro","displayName":"Gemini 2.5 Pro","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-embedding-001","supportedGenerationMethods":["embedContent"]},
              {"name":"models/gemini-2.5-flash-preview-tts","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-2.0-flash-live-001","supportedGenerationMethods":["bidiGenerateContent"]},
              {"name":"models/gemini-2.5-flash-image","supportedGenerationMethods":["generateContent"]},
              {"name":"models/embedding-001","supportedGenerationMethods":["embedContent"]},
              {"name":"models/imagen-4.0-generate","supportedGenerationMethods":["predict"]},
              {"name":"models/aqa","supportedGenerationMethods":["generateAnswer"]}]}
            """);

        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro"], models.Select(m => m.Id));
        Assert.Equal(LlmTest.SecretKey, request.Headers["x-goog-api-key"]);
        Assert.DoesNotContain(LlmTest.SecretKey, request.Url.ToString()); // header, not ?key=
    }

    [Theory]
    [InlineData(LlmProvider.OpenRouter)]
    [InlineData(LlmProvider.Anthropic)]
    [InlineData(LlmProvider.OpenAI)]
    [InlineData(LlmProvider.Gemini)]
    public async Task Provider_errors_while_listing_are_mapped(LlmProvider provider)
    {
        var clients = LlmTest.Clients(_ => StubTransport.Json("""{"error":"nope"}""", System.Net.HttpStatusCode.Unauthorized), out _);

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new LlmModelCatalog(clients).ListToolModelsAsync(provider, LlmTest.SecretKey, default));

        Assert.Equal(LlmFailureKind.InvalidKey, ex.Kind);
    }

    [Fact]
    public async Task Unexpected_json_gives_an_empty_list_or_a_rejection_not_a_crash()
    {
        Assert.Empty((await ListAsync(LlmProvider.OpenAI, "{}")).Models);
        Assert.Empty((await ListAsync(LlmProvider.OpenRouter, """{"data":[1,"x",null]}""")).Models);
        var clients = LlmTest.Clients(_ => StubTransport.Json("not json"), out _);
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new LlmModelCatalog(clients).ListToolModelsAsync(LlmProvider.OpenAI, LlmTest.SecretKey, default));
        Assert.Equal(LlmFailureKind.Rejected, ex.Kind);
    }
}
