using DocAgent.Infrastructure.Agents;
using DocAgent.Infrastructure.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocAgent.UnitTests.Agents;

public sealed class OllamaLimitsTests
{
    [Fact]
    public async Task The_development_route_carries_the_development_limits()
    {
        var agent = new AgentOptions();
        agent.Limits.Development = new AgentLimitsOptions { MaxToolCalls = 7, TimeoutSeconds = 33 };
        using var factory = new OllamaChatClientFactory(Options.Create(new LlmOptions()), NullLoggerFactory.Instance, Options.Create(agent));

        var route = await factory.GetRouteAsync();

        Assert.Equal(new AgentLimits(7, TimeSpan.FromSeconds(33)), route.Limits);
    }

    [Fact]
    public async Task Without_agent_options_the_route_still_gets_the_default_limits()
    {
        using var factory = new OllamaChatClientFactory(Options.Create(new LlmOptions()), NullLoggerFactory.Instance);

        Assert.Equal(new AgentLimits(10, TimeSpan.FromSeconds(120)), (await factory.GetRouteAsync()).Limits);
    }
}
