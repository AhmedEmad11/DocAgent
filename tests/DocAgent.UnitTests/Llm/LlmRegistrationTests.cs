using DocAgent.TestSupport;
using DocAgent.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocAgent.UnitTests.Llm;

/// <summary>Which no-key route is wired (stakeholder decision 2): Ollama only in Development/Testing, otherwise none until Phase 6.</summary>
public sealed class LlmRegistrationTests
{
    private static Type NoKeyType(bool development, string? setting)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            setting is null ? [] : new Dictionary<string, string?> { ["Llm:NoKeyRoute"] = setting }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLlm(config, development);
        return services.Single(d => d.ServiceType == typeof(INoKeyRouteProvider)).ImplementationType!;
    }

    [Theory]
    [InlineData(true, null, typeof(OllamaNoKeyRouteProvider))]
    [InlineData(true, "Auto", typeof(OllamaNoKeyRouteProvider))]
    [InlineData(false, null, typeof(NoNoKeyRouteProvider))]
    [InlineData(false, "Auto", typeof(NoNoKeyRouteProvider))]
    [InlineData(true, "None", typeof(NoNoKeyRouteProvider))]
    [InlineData(false, "Ollama", typeof(OllamaNoKeyRouteProvider))]
    public void No_key_route_follows_the_environment_and_the_switch(bool development, string? setting, Type expected) =>
        Assert.Equal(expected, NoKeyType(development, setting));

    [Fact]
    public void Unknown_no_key_route_fails_at_startup() =>
        Assert.Throws<InvalidOperationException>(() => NoKeyType(true, "Demo"));
}
