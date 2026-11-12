using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocAgent.Infrastructure.Llm;

public static class ChatClientPipeline
{
    public const string TelemetrySourceName = "DocAgent.Llm";

    /// <summary>
    /// Adds OpenTelemetry (token counts, no prompt content: sensitive data stays off) and logging.
    /// The logging middleware only writes message content at Trace level, so the Microsoft.Extensions.AI
    /// category is pinned to Warning in appsettings (SRS SEC-2: document text stays out of logs).
    /// </summary>
    public static IChatClient Wrap(IChatClient inner, ILoggerFactory loggerFactory) =>
        inner.AsBuilder()
            .UseOpenTelemetry(loggerFactory, TelemetrySourceName)
            .UseLogging(loggerFactory)
            .Build();
}
