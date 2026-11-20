using DocAgent.TestSupport;
using DocAgent.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocAgent.UnitTests.Llm;

/// <summary>Which no-key route is wired: Auto = Ollama in Development/Testing, the OpenRouter demo elsewhere (which needs the operator key).</summary>
public sealed class LlmRegistrationTests
{
    private static Type NoKeyType(bool development, string? setting, string? demoKey = "operator-key")
    {
        var settings = new Dictionary<string, string?> { ["Llm:Demo:OpenRouterApiKey"] = demoKey };
        if (setting is not null)
        {
            settings["Llm:NoKeyRoute"] = setting;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLlm(config, development);
        return services.Single(d => d.ServiceType == typeof(INoKeyRouteProvider)).ImplementationType!;
    }

    [Theory]
    [InlineData(true, null, typeof(OllamaNoKeyRouteProvider))]
    [InlineData(true, "Auto", typeof(OllamaNoKeyRouteProvider))]
    [InlineData(false, null, typeof(DemoNoKeyRouteProvider))]
    [InlineData(false, "Auto", typeof(DemoNoKeyRouteProvider))]
    [InlineData(true, "Demo", typeof(DemoNoKeyRouteProvider))]
    [InlineData(true, "None", typeof(NoNoKeyRouteProvider))]
    [InlineData(false, "Ollama", typeof(OllamaNoKeyRouteProvider))]
    public void No_key_route_follows_the_environment_and_the_switch(bool development, string? setting, Type expected) =>
        Assert.Equal(expected, NoKeyType(development, setting));

    [Fact]
    public void Unknown_no_key_route_fails_at_startup() =>
        Assert.Throws<InvalidOperationException>(() => NoKeyType(true, "Sometimes"));

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "Demo")]
    [InlineData(true, "Demo")]
    public void Demo_without_the_operator_key_fails_at_startup_with_a_clear_message(bool development, string? setting)
    {
        foreach (var key in new[] { null, "", "  " })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => NoKeyType(development, setting, key));
            Assert.Contains("Llm:Demo:OpenRouterApiKey", ex.Message);
            Assert.Contains("None", ex.Message);
        }
    }

    [Theory]
    [InlineData(true, "Auto")]
    [InlineData(true, "Ollama")]
    [InlineData(false, "None")]
    public void The_operator_key_is_only_required_for_the_demo_route(bool development, string setting) =>
        NoKeyType(development, setting, demoKey: null);
}
