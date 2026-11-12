using DocAgent.TestSupport;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocAgent.UnitTests.Llm;

public static class LlmTest
{
    public const string SecretKey = "sk-test-SECRET-0123456789abcdef-WXYZ";

    public static LlmHttpClients Clients(Func<StubTransport.RecordedRequest, HttpResponseMessage> respond, out StubTransport transport)
    {
        var stub = new StubTransport(respond);
        transport = stub;
        return new LlmHttpClients(_ => stub);
    }

    public static ProviderChatClientFactory Providers(LlmHttpClients clients) => new(clients, NullLoggerFactory.Instance);
}
