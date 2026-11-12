using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocAgent.IntegrationTests;

/// <summary>
/// Runs the real API in-process, pointed at the test PostgreSQL container.
/// </summary>
public sealed class DocAgentApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:docagentdb", connectionString);
    }
}
